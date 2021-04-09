
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using HarmonyLib;

namespace WebMap {
    //This attribute is required, and lists metadata for your plugin.
    //The GUID should be a unique ID for this plugin, which is human readable (as it is used in places like the config). I like to use the java package notation, which is "com.[your name here].[your plugin name here]"
    //The name is the name of the plugin that's displayed on load, and the version number just specifies what version the plugin is.
    [BepInPlugin("com.kylepaulsen.valheim.webmap", "WebMap", "1.0.0")]

    //This is the main declaration of our plugin class. BepInEx searches for all classes inheriting from BaseUnityPlugin to initialize on startup.
    //BaseUnityPlugin itself inherits from MonoBehaviour, so you can use this as a reference for what you can declare and use in your plugin class: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
    public class WebMap : BaseUnityPlugin {
        static readonly int TEXTURE_SIZE = 2048;
        static readonly int PIXEL_SIZE = 12;
        static readonly float EXPLORE_RADIUS = 100f;
        static readonly float UPDATE_FOG_TEXTURE_INTERVAL = 1f;
        static readonly float SAVE_FOG_TEXTURE_INTERVAL = 30f;

        static MapDataServer mapDataServer;
        static string worldDataPath;

        //The Awake() method is run at the very start when the game is initialized.
        public void Awake() {
            var harmony = new Harmony("com.kylepaulsen.valheim.webmap");
            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), (string) null);

            string[] arguments = Environment.GetCommandLineArgs();
            var worldName = "";
            for (var t = 0; t < arguments.Length; t++) {
                if (arguments[t] == "-world") {
                    worldName = arguments[t + 1];
                    break;
                }
            }

            var pluginPath = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var mapDataPath = Path.Combine(pluginPath, "map_data");
            Directory.CreateDirectory(mapDataPath);
            worldDataPath = Path.Combine(mapDataPath, worldName);
            Directory.CreateDirectory(worldDataPath);

            mapDataServer = new MapDataServer();
            mapDataServer.ListenAsync();

            var mapImagePath = Path.Combine(worldDataPath, "map");
            try {
                mapDataServer.mapImageData = File.ReadAllBytes(mapImagePath);
            } catch (Exception e) {
                Debug.Log("Failed to read map image data from disk. " + e.Message);
            }

            var fogImagePath = Path.Combine(worldDataPath, "fog.png");
            try {
                var fogTexture = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE);
                var fogBytes = File.ReadAllBytes(fogImagePath);
                fogTexture.LoadImage(fogBytes);
                mapDataServer.fogTexture = fogTexture;
            } catch (Exception e) {
                Debug.Log("Failed to read fog image data from disk... Making new fog image..." + e.Message);
                var fogTexture = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE, TextureFormat.RGB24, false);
                var fogColors = new Color32[TEXTURE_SIZE * TEXTURE_SIZE];
                for (var t = 0; t < fogColors.Length; t++) {
                    fogColors[t] = Color.black;
                }
                fogTexture.SetPixels32(fogColors);
                var fogPngBytes = fogTexture.EncodeToPNG();

                mapDataServer.fogTexture = fogTexture;
                try {
                    File.WriteAllBytes(fogImagePath, fogPngBytes);
                } catch {
                    Debug.Log("FAILED TO WRITE FOG FILE!");
                }
            }

            InvokeRepeating("UpdateFogTexture", UPDATE_FOG_TEXTURE_INTERVAL, UPDATE_FOG_TEXTURE_INTERVAL);
            InvokeRepeating("SaveFogTexture", SAVE_FOG_TEXTURE_INTERVAL, SAVE_FOG_TEXTURE_INTERVAL);
        }

        public void UpdateFogTexture() {
            int pixelExploreRadius = (int)Mathf.Ceil(EXPLORE_RADIUS / PIXEL_SIZE);
            int pixelExploreRadiusSquared = pixelExploreRadius * pixelExploreRadius;
            var halfTextureSize = TEXTURE_SIZE / 2;

            mapDataServer.players.ForEach(player => {
                if (player.m_publicRefPos) {
                    var zdoData = ZDOMan.instance.GetZDO(player.m_characterID);
                    var pos = zdoData.GetPosition();
                    var pixelX = Mathf.RoundToInt(pos.x / PIXEL_SIZE + halfTextureSize);
                    var pixelY = Mathf.RoundToInt(pos.z / PIXEL_SIZE + halfTextureSize);
                    for (var y = pixelY - pixelExploreRadius; y <= pixelY + pixelExploreRadius; y++) {
                        for (var x = pixelX - pixelExploreRadius; x <= pixelX + pixelExploreRadius; x++) {
                            if (y >= 0 && x >= 0 && y < TEXTURE_SIZE && x < TEXTURE_SIZE) {
                                var xDiff = pixelX - x;
                                var yDiff = pixelY - y;
                                var currentExploreRadiusSquared = xDiff * xDiff + yDiff * yDiff;
                                if (currentExploreRadiusSquared < pixelExploreRadiusSquared) {
                                    mapDataServer.fogTexture.SetPixel(x, y, Color.white);
                                }
                            }
                        }
                    }
                }
            });
        }

        public void SaveFogTexture() {
            if (mapDataServer.players.Count > 0) {
                byte[] pngBytes = mapDataServer.fogTexture.EncodeToPNG();

                Debug.Log("Saving fog file...");
                try {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "fog.png"), pngBytes);
                } catch {
                    Debug.Log("FAILED TO WRITE FOG FILE!");
                }
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
                if (mapDataServer.mapImageData != null) {
                    Debug.Log("MAP ALREADY BUILT!");
                    return;
                }
                Debug.Log("BUILD MAP!");

                int num = TEXTURE_SIZE / 2;
                float num2 = PIXEL_SIZE / 2f;
                Color32[] colorArray = new Color32[TEXTURE_SIZE * TEXTURE_SIZE];
                Color32[] treeMaskArray = new Color32[TEXTURE_SIZE * TEXTURE_SIZE];
                float[] heightArray = new float[TEXTURE_SIZE * TEXTURE_SIZE];
                for (int i = 0; i < TEXTURE_SIZE; i++) {
                    for (int j = 0; j < TEXTURE_SIZE; j++) {
                        float wx = (float)(j - num) * PIXEL_SIZE + num2;
                        float wy = (float)(i - num) * PIXEL_SIZE + num2;
                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(wx, wy);
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy);
                        colorArray[i * TEXTURE_SIZE + j] = GetPixelColor(biome);
                        treeMaskArray[i * TEXTURE_SIZE + j] = GetMaskColor(wx, wy, biomeHeight, biome);
                        heightArray[i * TEXTURE_SIZE + j] = biomeHeight;
                    }
                }

                var waterLevel = ZoneSystem.instance.m_waterLevel;
                var sunDir = new Vector3(-0.57735f, 0.57735f, 0.57735f);
                var newColors = new Color[colorArray.Length];

                for (var t = 0; t < colorArray.Length; t++) {
                    var h = heightArray[t];

                    var tUp = t - TEXTURE_SIZE;
                    if (tUp < 0) {
                        tUp = t;
                    }
                    var tDown = t + TEXTURE_SIZE;
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

                var newTexture = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE, TextureFormat.RGBA32, false);
                newTexture.SetPixels(newColors);
                byte[] pngBytes = newTexture.EncodeToPNG();

                mapDataServer.mapImageData = pngBytes;
                try {
                    File.WriteAllBytes(Path.Combine(worldDataPath, "map"), pngBytes);
                } catch {
                    Debug.Log("FAILED TO WRITE MAP FILE!");
                }
                Debug.Log("BUILDING MAP DONE!");
            }
        }

        [HarmonyPatch(typeof (ZNet), "Start")]
        private class ZNetPatch {
            static void Postfix(List<ZNetPeer> ___m_peers) {
                mapDataServer.players = ___m_peers;
            }
        }
    }
}
