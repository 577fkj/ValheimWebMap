
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using HarmonyLib;
using WebSocketSharp;
using static WebMap.WebMapConfig;

using static ZRoutedRpc;

namespace WebMap {
    //This attribute is required, and lists metadata for your plugin.
    //The GUID should be a unique ID for this plugin, which is human readable (as it is used in places like the config). I like to use the java package notation, which is "com.[your name here].[your plugin name here]"
    //The name is the name of the plugin that's displayed on load, and the version number just specifies what version the plugin is.
    [BepInPlugin("no.runnane.valheim.webmap", "WebMap", "2.0.0")]

    //This is the main declaration of our plugin class. BepInEx searches for all classes inheriting from BaseUnityPlugin to initialize on startup.
    //BaseUnityPlugin itself inherits from MonoBehaviour, so you can use this as a reference for what you can declare and use in your plugin class: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
    public class WebMap : BaseUnityPlugin {

        static readonly DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        static readonly HashSet<string> ALLOWED_PINS = new HashSet<string> { "dot", "fire", "mine", "house", "cave" };

        static MapDataServer mapDataServer;
        static string worldDataPath;

        private bool fogTextureNeedsSaving = false;
        public static BepInEx.Logging.ManualLogSource harmonyLog;
        public Harmony harmony;

        //The Awake() method is run at the very start when the game is initialized.
        public void Awake() {
            
            harmony = new Harmony("no.runnane.valheim.webmap");
            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), (string) null);

            harmonyLog = Logger;

            var pluginPath = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            WebMapConfig.readConfigFile(Path.Combine(pluginPath, "config.json"));

            var mapDataPath = Path.Combine(pluginPath, "map_data");
            Directory.CreateDirectory(mapDataPath);
            worldDataPath = Path.Combine(mapDataPath, WebMapConfig.getWorldName());
            Directory.CreateDirectory(worldDataPath);

            mapDataServer = new MapDataServer();
            mapDataServer.ListenAsync();


            var mapImagePath = Path.Combine(worldDataPath, "map");
            try {
                mapDataServer.mapImageData = File.ReadAllBytes(mapImagePath);
            } catch (Exception e) {
                Logger.LogError("WebMap: Failed to read map image data from disk. " + e.Message);
            }

            var fogImagePath = Path.Combine(worldDataPath, "fog.png");
            try {
                var fogTexture = new Texture2D(WebMapConfig.TEXTURE_SIZE, WebMapConfig.TEXTURE_SIZE);
                var fogBytes = File.ReadAllBytes(fogImagePath);
                fogTexture.LoadImage(fogBytes);
                mapDataServer.fogTexture = fogTexture;
            } catch (Exception e) {
                Logger.LogWarning("WebMap: Failed to read fog image data from disk... Making new fog image..." + e.Message);
                var fogTexture = new Texture2D(WebMapConfig.TEXTURE_SIZE, WebMapConfig.TEXTURE_SIZE, TextureFormat.RGB24, false);
                var fogColors = new Color32[WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE];
                for (var t = 0; t < fogColors.Length; t++) {
                    fogColors[t] = Color.black;
                }
                fogTexture.SetPixels32(fogColors);
                var fogPngBytes = fogTexture.EncodeToPNG();

                mapDataServer.fogTexture = fogTexture;
                try {
                    File.WriteAllBytes(fogImagePath, fogPngBytes);
                } catch (Exception e2)
                {
                    harmonyLog.LogError("WebMap: FAILED TO WRITE FOG FILE! " + e2);
                }
            }

            InvokeRepeating("UpdateFogTexture", WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL, WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL);
            InvokeRepeating("SaveFogTexture", WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL, WebMapConfig.SAVE_FOG_TEXTURE_INTERVAL);

            InvokeRepeating("SavePinsIfNeeded", WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL, WebMapConfig.UPDATE_FOG_TEXTURE_INTERVAL);

            InvokeRepeating("BroadcastWebsocket", WebMapConfig.PLAYER_UPDATE_INTERVAL, WebMapConfig.PLAYER_UPDATE_INTERVAL);

            var mapPinsFile = Path.Combine(worldDataPath, "pins.csv");
            try {
                var pinsLines = File.ReadAllLines(mapPinsFile);
                mapDataServer.pins = new List<string>(pinsLines);
            } catch (Exception e) {
                harmonyLog.LogError("WebMap: Failed to read pins.csv from disk. " + e.Message);
            }
        }

        public void BroadcastWebsocket()
        {


            var dataString = "";
            mapDataServer.players.ForEach(player =>
            {
                ZDO zdoData = null;
                try
                {
                    zdoData = ZDOMan.instance.GetZDO(player.m_characterID);
                }
                catch { }

                if (zdoData != null)
                {
                    var pos = zdoData.GetPosition();
                    var maxHealth = zdoData.GetFloat("max_health", 25f);
                    var health = zdoData.GetFloat("health", maxHealth);
                    maxHealth = Mathf.Max(maxHealth, health);

                    var maxStamina = zdoData.GetFloat("max_stamina", 100f);
                    var stamina = zdoData.GetFloat("stamina", maxStamina);
                    //  maxStamina = Mathf.Max(maxStamina, stamina);

                    if (player.m_publicRefPos)
                    {
                        dataString +=
                            $"{player.m_uid}\n{player.m_playerName}\n{str(pos.x)},{str(pos.y)},{str(pos.z)}\n{str(health)}\n{str(maxHealth)}\n{str(stamina)}\n{str(maxStamina)}\n\n";
                    }
                    else
                    {
                        dataString += $"{player.m_uid}\n{player.m_playerName}\nhidden\n\n";
                    }
                   
                }
                else
                {
                    
                }
            });
            if (dataString.Length > 0)
            {
                mapDataServer.Broadcast("players\n" + dataString.Trim());
               
            }
            mapDataServer.Broadcast("time\n" + GetCurrentTimeString());
           
        }

        public void SavePinsIfNeeded()
        {
            if (mapDataServer.NeedSave)
            {
                SavePins();
                mapDataServer.NeedSave = false;
            }
        }


        public void UpdateFogTexture() {
           
            int pixelExploreRadius = (int)Mathf.Ceil(WebMapConfig.EXPLORE_RADIUS / WebMapConfig.PIXEL_SIZE);
            int pixelExploreRadiusSquared = pixelExploreRadius * pixelExploreRadius;
            var halfTextureSize = WebMapConfig.TEXTURE_SIZE / 2;
        

            mapDataServer.players.ForEach(player => {
                if (player.m_publicRefPos) {
                    ZDO zdoData = null;
                    try {
                        zdoData = ZDOMan.instance.GetZDO(player.m_characterID);
                    } catch {}
                    if (zdoData != null) {
                        var pos = zdoData.GetPosition();
                        var pixelX = Mathf.RoundToInt(pos.x / WebMapConfig.PIXEL_SIZE + halfTextureSize);
                        var pixelY = Mathf.RoundToInt(pos.z / WebMapConfig.PIXEL_SIZE + halfTextureSize);
                        for (var y = pixelY - pixelExploreRadius; y <= pixelY + pixelExploreRadius; y++) {
                            for (var x = pixelX - pixelExploreRadius; x <= pixelX + pixelExploreRadius; x++) {
                                if (y >= 0 && x >= 0 && y < WebMapConfig.TEXTURE_SIZE && x < WebMapConfig.TEXTURE_SIZE) {
                                    var xDiff = pixelX - x;
                                    var yDiff = pixelY - y;
                                    var currentExploreRadiusSquared = xDiff * xDiff + yDiff * yDiff;
                                    if (currentExploreRadiusSquared < pixelExploreRadiusSquared) {
                                        var fogTexColor = mapDataServer.fogTexture.GetPixel(x, y);
                                        if (fogTexColor.r < 1f) {
                                            fogTextureNeedsSaving = true;
                                            mapDataServer.fogTexture.SetPixel(x, y, Color.clear);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
        }

        public void SaveFogTexture() {
           
            if (mapDataServer.players.Count > 0 && fogTextureNeedsSaving) {
                byte[] pngBytes = mapDataServer.fogTexture.EncodeToPNG();

              
                try {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "fog.png"), pngBytes);
                    fogTextureNeedsSaving = false;
                   
                } catch {
                    Logger.LogError("WebMap: FAILED TO WRITE FOG FILE!");
                }
            }
        }

        public static void SavePins() {
            var mapPinsFile = Path.Combine(worldDataPath, "pins.csv");
            try {
                File.WriteAllLines(mapPinsFile, mapDataServer.pins);
               // harmonyLog.Log("WebMap: Wrote pins file");
            } catch {
                harmonyLog.LogError("WebMap: FAILED TO WRITE PINS FILE!");
            }
        }

        // TODO: implement "person has logged in"
        [HarmonyPatch(typeof(ZRoutedRpc), "AddPeer")]
        private class ZRoutedRpcAddPeerPatch
        {
            static void Prefix(ZNetPeer peer)
            {
                harmonyLog.LogInfo("ZRoutedRpc.AddPeer(): " + peer.m_playerName);
                mapDataServer.Broadcast($"login\n{peer.m_playerName}");

            
            }
        }


        // TODO: implement "person has logged out"
        [HarmonyPatch(typeof(ZRoutedRpc), "RemovePeer")]
        private class ZRoutedRpcRemovePeerPatch
        {
            static void Prefix(ZNetPeer peer)
            {
                harmonyLog.LogInfo("ZRoutedRpc.RemovePeer(): " + peer.m_playerName);
                mapDataServer.Broadcast($"logout\n{peer.m_playerName}");
            }
        }


        [HarmonyPatch(typeof (ZoneSystem), "Start")]
        private class ZoneSystemPatch {

            static readonly Color DeepWaterColor = new Color(0.36105883f, 0.36105883f, 0.43137255f);
            static readonly Color ShallowWaterColor = new Color(0.574f, 0.50709206f, 0.47892025f);
            static readonly Color ShoreColor = new Color(0.1981132f, 0.12241901f, 0.1503943f);

            static Color GetMaskColor(float wx, float wy, float height, Heightmap.Biome biome) {
                var noForest = new Color(0f, 0f, 0f, 0f);
                var forest = new Color(1f, 0f, 0f, 0f);

                if (height < ZoneSystem.instance.m_waterLevel) {
                    return noForest;
                }
                if (biome == Heightmap.Biome.Meadows) {
                    if (!WorldGenerator.InForest(new Vector3(wx, 0f, wy))) {
                        return noForest;
                    }
                    return forest;
                } else if (biome == Heightmap.Biome.Plains) {
                    if (WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wy)) >= 0.8f) {
                        return noForest;
                    }
                    return forest;
                } else {
                    if (biome == Heightmap.Biome.BlackForest || biome == Heightmap.Biome.Mistlands) {
                        return forest;
                    }
                    return noForest;
                }
            }

            static Color GetPixelColor(Heightmap.Biome biome) {
                var m_meadowsColor = new Color(0.573f, 0.655f, 0.361f);
                var m_swampColor = new Color(0.639f, 0.447f, 0.345f);
                var m_mountainColor = new Color(1f, 1f, 1f);
                var m_blackforestColor = new Color(0.420f, 0.455f, 0.247f);
                var m_heathColor = new Color(0.906f, 0.671f, 0.470f);
                var m_ashlandsColor = new Color(0.690f, 0.192f, 0.192f);
                var m_deepnorthColor = new Color(1f, 1f, 1f);
                var m_mistlandsColor = new Color(0.325f, 0.325f, 0.325f);

                if (biome <= Heightmap.Biome.Plains) {
                    switch (biome) {
                        case Heightmap.Biome.Meadows:
                            return m_meadowsColor;
                        case Heightmap.Biome.Swamp:
                            return m_swampColor;
                        case (Heightmap.Biome)3:
                            break;
                        case Heightmap.Biome.Mountain:
                            return m_mountainColor;
                        default:
                            if (biome == Heightmap.Biome.BlackForest) {
                                return m_blackforestColor;
                            }
                            if (biome == Heightmap.Biome.Plains) {
                                return m_heathColor;
                            }
                            break;
                    }
                } else if (biome <= Heightmap.Biome.DeepNorth) {
                    if (biome == Heightmap.Biome.AshLands) {
                        return m_ashlandsColor;
                    }
                    if (biome == Heightmap.Biome.DeepNorth) {
                        return m_deepnorthColor;
                    }
                } else {
                    if (biome == Heightmap.Biome.Ocean) {
                        return Color.white;
                    }
                    if (biome == Heightmap.Biome.Mistlands) {
                        return m_mistlandsColor;
                    }
                }
                return Color.white;
            }

            static void Postfix(ZoneSystem __instance) {
                Vector3 startPos;
                ZoneSystem.instance.GetLocationIcon("StartTemple", out startPos);
                WebMapConfig.WORLD_START_POS = startPos;

                if (mapDataServer.mapImageData != null) {
                    harmonyLog.LogInfo("WebMap: MAP ALREADY BUILT!");
                    return;
                }
                harmonyLog.LogInfo("WebMap: BUILD MAP!");

                int num = WebMapConfig.TEXTURE_SIZE / 2;
                float num2 = WebMapConfig.PIXEL_SIZE / 2f;
                Color32[] colorArray = new Color32[WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE];
                Color32[] treeMaskArray = new Color32[WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE];
                float[] heightArray = new float[WebMapConfig.TEXTURE_SIZE * WebMapConfig.TEXTURE_SIZE];
                for (int i = 0; i < WebMapConfig.TEXTURE_SIZE; i++) {
                    for (int j = 0; j < WebMapConfig.TEXTURE_SIZE; j++) {
                        float wx = (float)(j - num) * WebMapConfig.PIXEL_SIZE + num2;
                        float wy = (float)(i - num) * WebMapConfig.PIXEL_SIZE + num2;
                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(wx, wy);
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy, out Color _);
                        colorArray[i * WebMapConfig.TEXTURE_SIZE + j] = GetPixelColor(biome);
                        treeMaskArray[i * WebMapConfig.TEXTURE_SIZE + j] = GetMaskColor(wx, wy, biomeHeight, biome);
                        heightArray[i * WebMapConfig.TEXTURE_SIZE + j] = biomeHeight;
                    }
                }

                var waterLevel = ZoneSystem.instance.m_waterLevel;
                var sunDir = new Vector3(-0.57735f, 0.57735f, 0.57735f);
                var newColors = new Color[colorArray.Length];

                for (var t = 0; t < colorArray.Length; t++) {
                    var h = heightArray[t];

                    var tUp = t - WebMapConfig.TEXTURE_SIZE;
                    if (tUp < 0) {
                        tUp = t;
                    }
                    var tDown = t + WebMapConfig.TEXTURE_SIZE;
                    if (tDown > colorArray.Length - 1) {
                        tDown = t;
                    }
                    var tRight = t + 1;
                    if (tRight > colorArray.Length - 1) {
                        tRight = t;
                    }
                    var tLeft = t - 1;
                    if (tLeft < 0) {
                        tLeft = t;
                    }
                    var hUp = heightArray[tUp];
                    var hRight = heightArray[tRight];
                    var hLeft = heightArray[tLeft];
                    var hDown = heightArray[tDown];

                    var va = new Vector3(2f, 0f, hRight - hLeft).normalized;
                    var vb = new Vector3(0f, 2f, hUp - hDown).normalized;
                    var normal = Vector3.Cross(va, vb);

                    var surfaceLight = Vector3.Dot(normal, sunDir) * 0.25f + 0.75f;

                    float shoreMask = Mathf.Clamp(h - waterLevel, 0, 1);
                    float shallowRamp = Mathf.Clamp((h - waterLevel + 0.2f * 12.5f) * 0.5f, 0, 1);
                    float deepRamp = Mathf.Clamp((h - waterLevel + 1f * 12.5f) * 0.1f, 0, 1);

                    var mapColor = colorArray[t];
                    Color ans = Color.Lerp(ShoreColor, mapColor, shoreMask);
                    ans = Color.Lerp(ShallowWaterColor, ans, shallowRamp);
                    ans = Color.Lerp(DeepWaterColor, ans, deepRamp);

                    newColors[t] = new Color(ans.r * surfaceLight, ans.g * surfaceLight, ans.b * surfaceLight, ans.a);
                }

                var newTexture = new Texture2D(WebMapConfig.TEXTURE_SIZE, WebMapConfig.TEXTURE_SIZE, TextureFormat.RGBA32, false);
                newTexture.SetPixels(newColors);
                byte[] pngBytes = newTexture.EncodeToPNG();

                mapDataServer.mapImageData = pngBytes;
                try {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "map"), pngBytes);
                    harmonyLog.LogInfo("WebMap: BUILDING MAP DONE!");
                } catch {
                    harmonyLog.LogError("WebMap: FAILED TO WRITE MAP FILE!");
                }
                
            }
        }

        [HarmonyPatch(typeof (ZNet), "Start")]
        private class ZNetPatch {
            static void Postfix(List<ZNetPeer> ___m_peers) {
                mapDataServer.players = ___m_peers;
            }
        }

        [HarmonyPatch(typeof (ZRoutedRpc), "HandleRoutedRPC")]
        private class ZRoutedRpcHandleRoutedRPCPatch
        {
            static void Prefix(RoutedRPCData data)
            {

                var targetName = "";
                ZDO targetZDO;
                if (!data.m_targetZDO.IsNone())
                {
                    try
                    {
                        targetZDO = ZDOMan.instance.GetZDO(data.m_targetZDO);
                        targetName = targetZDO.m_type.ToString();
                    }
                    catch { }
                }
                

                ZNetPeer peer = ZNet.instance.GetPeer(data.m_senderPeerID);
                var steamid = "";
                try {
                    steamid = peer.m_rpc.GetSocket().GetHostName();
                } catch {}

                if (data?.m_methodHash == "Say".GetStableHashCode()) {
                    // private void RPC_Say(long sender, int ctype, string user, string text)

                    try
                    {
                        var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        var pos = zdoData.GetPosition();
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());
                        int messageType = package.ReadInt();
                        string userName = package.ReadString();
                        string message = package.ReadString();
                        message = (message == null ? "" : message).Trim();

                        if (message.StartsWith("/pin")) {
                            var messageParts = message.Split(' ');
                            var pinType = "dot";
                            var startIdx = 1;
                            if (messageParts.Length > 1 && ALLOWED_PINS.Contains(messageParts[1])) {
                                pinType = messageParts[1];
                                startIdx = 2;
                            }
                            var pinText = "";
                            if (startIdx < messageParts.Length) {
                                pinText = String.Join(" ", messageParts, startIdx, messageParts.Length - startIdx);
                            }
                            if (pinText.Length > 40) {
                                pinText = pinText.Substring(0, 40);
                              
                            }
                            var safePinsText = Regex.Replace(pinText, @"[^a-zA-Z0-9 ]", "");
                            var uuId = Guid.NewGuid();
                            var pinId = uuId.ToString();
                            mapDataServer.AddPin(steamid, pinId, pinType, userName, pos, safePinsText);

                            var usersPins = mapDataServer.pins.FindAll(pin => pin.StartsWith(steamid));
                            
                            var numOverflowPins = usersPins.Count - WebMapConfig.MAX_PINS_PER_USER;
                            for (var t = numOverflowPins; t > 0; t--) {
                                var pinIdx = mapDataServer.pins.FindIndex(pin => pin.StartsWith(steamid));
                                // mapDataServer.RemovePin(pinIdx);
                                harmonyLog.LogInfo($"To many pins, deleting oldest one (ONLY DEBUG; WILL NOT DO)");
                            }
                            
                            SavePins();
                        } else if (message.StartsWith("/undoPin")) {
                            var pinIdx = mapDataServer.pins.FindLastIndex(pin => pin.StartsWith(steamid));
                            if (pinIdx > -1) {
                                mapDataServer.RemovePin(pinIdx);
                                SavePins();
                            }
                        } else if (message.StartsWith("/deletePin")) {
                            var messageParts = message.Split(' ');
                            var pinText = "";
                            if (messageParts.Length > 1) {
                                pinText = String.Join(" ", messageParts, 1, messageParts.Length - 1);
                            }

                            var pinIdx = mapDataServer.pins.FindLastIndex(pin => {
                                var pinParts = pin.Split(',');
                                return pinParts[0] == steamid && pinParts[pinParts.Length - 1] == pinText;
                            });

                            if (pinIdx > -1) {
                                mapDataServer.RemovePin(pinIdx);
                                SavePins();
                            }
                        }
                        else if(!message.StartsWith("/"))
                        {
                            mapDataServer.Broadcast($"say\n{messageType}\n{userName}\n{message}");
                            harmonyLog.LogInfo($"say\n{messageType}\n{userName}\n{message} / target={targetName}");
                        }
                    }
                    catch
                    {
                        harmonyLog.LogError($"Say() exception");
                    }

                } else if (data?.m_methodHash == "ChatMessage".GetStableHashCode()) {
                    // private void RPC_ChatMessage(long sender, Vector3 position, int type, string name, string text)
                    try
                    {
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());
                        Vector3 pos = package.ReadVector3();
                        int messageType = package.ReadInt();
                        string userName = package.ReadString();
                        string message = package.ReadString();
                        message = (message == null ? "" : message).Trim();

                        if (messageType == (int)Talker.Type.Ping) {
                            mapDataServer.BroadcastPing(data.m_senderPeerID, userName, pos);
                        }
                        else
                        {
                            mapDataServer.Broadcast($"chat\n{messageType}\n{userName}\n{pos}\n{message}");
                            harmonyLog.LogInfo($"ChatMessage() chat\n{messageType}\n{userName}\n{pos}\n{message} / target={targetName}");
                        }

                    }
                    catch
                    {
                        harmonyLog.LogError($"ChatMessage() exception");
                    }
                }
                else if(data?.m_methodHash == "OnDeath".GetStableHashCode())
                {
                    // private void RPC_OnDeath(long sender)
                    try
                    {
                        var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        var pos = zdoData.GetPosition();
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());

                        mapDataServer.Broadcast($"ondeath\n{peer.m_playerName}");
                        harmonyLog.LogInfo($"RPC_OnDeath() -- {peer.m_playerName} / target={targetName}");
                    }
                    catch
                    {
                        harmonyLog.LogError($"RPC_OnDeath() exception");
                    }
                }
                else if (data?.m_methodHash == "Message".GetStableHashCode())
                {
                    // private void RPC_Message(long sender, int type, string msg, int amount)
                    try
                    {
                        var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        var pos = zdoData.GetPosition();
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());

                        int messageType = package.ReadInt();
                        string msg = package.ReadString();
                        int amount = package.ReadInt();

                        mapDataServer.Broadcast($"message\n{peer.m_playerName}\n{messageType}\n{msg}\n{amount}");
                        harmonyLog.LogInfo($"RPC_Message() -- {peer.m_playerName} / {msg} - {amount} / target={targetName}");
                    }
                    catch
                    {
                        harmonyLog.LogError($"RPC_Message() exception");

                    }
                }
             
                else if (data?.m_methodHash == "DamageText".GetStableHashCode())
                {
                    try
                    { 
                        var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        var pos = zdoData.GetPosition();
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());

                        //float v = package.Read();
                        //bool alerted = package.ReadBool();
                        var pkg = package.ReadPackage();

                        DamageText.TextType type = (DamageText.TextType)pkg.ReadInt();
                        Vector3 vector = pkg.ReadVector3();
                        float dmg = pkg.ReadSingle();
                        bool flag = pkg.ReadBool();

                        harmonyLog.LogInfo($"RPC_DamageText() -- {peer.m_playerName} / type={type} / pos={vector} / dmg={dmg} / flag={flag} / target={targetName}");
                    }
                    catch
                    {
                        harmonyLog.LogError($"RPC_DamageText() exception");
                    }
                }
                else if (data?.m_methodHash == "Damage".GetStableHashCode())
                {
                    try
                    {
                        var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        var pos = zdoData.GetPosition();
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());

                        harmonyLog.LogInfo($"RPC_Damage() -- {peer.m_playerName} / target={targetName}");
                    }
                    catch
                    {
                        harmonyLog.LogError($"RPC_Damage() exception");
                    }
                }
                else if (data?.m_methodHash == "DestroyZDO".GetStableHashCode())
                {
                    try
                    {
                        // supress
                        //ZPackage pkg = new ZPackage(data.m_parameters.GetArray());
                        //var pkg2 = pkg.ReadPackage();
                        //var numberofitems = pkg2.ReadInt();
                        //for (int i = 0; i < numberofitems; i++)
                        //{
                        //    ZDOID uid = pkg.ReadZDOID();
                            
                        //}
                        //harmonyLog.LogInfo($"DestroyZDO() -- {peer.m_playerName} / numberofitems={numberofitems} / target={targetName}");
                    }
                    catch (Exception e)
                    {
                        harmonyLog.LogError($"DestroyZDO() exception " + e);
                    }
                }
                else if (data?.m_methodHash == "SetEvent".GetStableHashCode())
                {
                    try
                    {
                        //   var zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        //   var pos = zdoData.GetPosition();

                        ZPackage pkg = new ZPackage(data.m_parameters.GetArray());

                        var eventName = pkg.ReadString();
                        var time = pkg.ReadSingle();
                        var eventPos = pkg.ReadVector3();


                        if (!eventName.IsNullOrEmpty())
                        {
                            harmonyLog.LogInfo($"SetEvent() -- eventName={eventName} / time={time} / eventPos={eventPos} / target={targetName}");
                        }
                        
                    }
                    catch (Exception e)
                    {
                        harmonyLog.LogError($"SetEvent() exception " + e);
                    }
                }
                else
                {
                    // Debug 


                    //var methods = new List<string>() { "Jump", "OnJump", "SetMoveDir", "AddDPS", "AddFire", "BlockAttack", "UseStamina", "OnTargeted", "SetHealth", "SetCrouch", 
                    //    "SetLookDir","SetRun", "Stagger", "Grow", "Shake", "CreateFragments", "RemotePrint","Pickup","Move","Die","Destroy","Awake","Loot","AddOre","EmptyProcessed",
                    //    "Interact","Hit","Create","Start","UseItem","UpdateTeleport","UseDoor","DropItem","AddNoise","Alert","Pick","Forward","Stop","OnHit","AddStatusEffect",
                    //    "Heal","AddFuel","OnNearProjectileHit","SleepStop","SleepStart","Ping", "Pong","DiscoverLocationRespons", "DiscoverClosestLocation",
                    //    "DestroyZDO","RequestZDO","SpawnObject","SetGlobalKey","GlobalKeys","LocationIcons","SetOwner","Extract","ResetCloth","SetTamed","RequestOpen","OpenRespons",
                    //    "RequestTakeAll", "TakeAllRespons","RequestOpen","SetSlotVisual","RemoveDoneItem","AddItem","Tap","Pickup","RequestPickup","Nibble","Step","SetPicked",
                    //    "ApplyOperation"


                    //};
                    //bool found = false;
                    //foreach (string method in methods)
                    //{
                    //    if (data?.m_methodHash == method.GetStableHashCode())
                    //    {
                    //        found = true;
                    //        harmonyLog.LogInfo($" -> DEBUG: {method}() ");
                    //    }
                        
                    //}

                    //if (!found)
                    //{
                    //    // Unknown RPC message
                    //    harmonyLog.LogInfo($"<unknown rpc message> hash={data.m_methodHash}");
                    //}
                   

                }
            }
            
        }


        private string GetCurrentTimeString()
        {
            if (!EnvMan.instance)
                return "";
            float fraction = (float)typeof(EnvMan).GetField("m_smoothDayFraction", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(EnvMan.instance);

            int hour = (int)(fraction * 24);
            int minute = (int)((fraction * 24 - hour) * 60);
            int second = (int)((((fraction * 24 - hour) * 60) - minute) * 60);

            DateTime now = DateTime.Now;
            DateTime theTime = new DateTime(now.Year, now.Month, now.Day, hour, minute, second);
            int days = Traverse.Create(EnvMan.instance).Method("GetCurrentDay").GetValue<int>();
            return "" + days.ToString() + ","+ fraction + "," + theTime.ToString("HH:mm:ss");
          
        }

     
    }


}
