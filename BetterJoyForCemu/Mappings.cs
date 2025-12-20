using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;

namespace BetterJoyForCemu {
    public static class Mappings {
        private static Dictionary<string, Joycon.Button> map = new Dictionary<string, Joycon.Button>();
        private static string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JoyconRemap.txt");
        public static MainForm form = null;

        private static Dictionary<string, float> floatMap = new Dictionary<string, float>();
        private static Dictionary<string, string> stringMap = new Dictionary<string, string>();
        
        static Mappings() {
            Load();
        }

        public static void Load() {
            map.Clear();
            floatMap.Clear();
            stringMap.Clear();
            if (!File.Exists(path)) {
                CreateDefault();
            }

            try {
                if (form != null) form.AppendTextBox($"Loading {path}...\r\n");
                var lines = File.ReadAllLines(path);
                int count = 0;
                foreach (var line in lines) {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//")) continue;
                    var parts = line.Split('=');
                    if (parts.Length == 2) {
                        string key = parts[0].Trim();
                        string val = parts[1].Trim();
                        
                        stringMap[key] = val;

                        // Try parsing as Button Enum
                        try {
                            if (val.ToUpper() == "NONE") {
                                map[key] = Joycon.Button.NONE;
                                count++;
                                continue;
                            }
                            Joycon.Button btn = (Joycon.Button)Enum.Parse(typeof(Joycon.Button), val, true);
                            map[key] = btn;
                            count++;
                            continue;
                        } catch { }

                        // Try parsing as Float
                        try {
                            float f = float.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
                            floatMap[key] = f;
                            continue;
                        } catch { }
                    }
                }
                if (form != null) form.AppendTextBox($"Loaded {count} mappings from JoyconRemap.txt\r\n");
            } catch (Exception e) {
                if (form != null) form.AppendTextBox($"Error loading mappings: {e.Message}\r\n");
            }
        }

        private static void CreateDefault() {
            try {
                using (StreamWriter w = new StreamWriter(path)) {
                    w.WriteLine("# Joycon Button & Gyro Remapping Configuration");
                    w.WriteLine("# Format: Key = Value");
                    w.WriteLine("# Valid Physical Buttons: DPAD_DOWN, DPAD_RIGHT, DPAD_LEFT, DPAD_UP, SL, SR, MINUS, HOME, PLUS, CAPTURE, STICK, SHOULDER_1, SHOULDER_2, B, A, Y, X, STICK2, SHOULDER2_1, SHOULDER2_2, NONE");
                    w.WriteLine("# Gyro: 1.0, -1.0 for direction");
                    w.WriteLine("");

                    string[] orientations = { "Horizontal", "Vertical" };
                    string[] sides = { "Left", "Right" };
                    string[] buttons = { "Face_A", "Face_B", "Face_X", "Face_Y", "Dpad_Up", "Dpad_Down", "Dpad_Left", "Dpad_Right", "Shoulder_L", "Shoulder_R", "Trigger_L", "Trigger_R", "Back", "Start", "Guide", "Stick_Click_L", "Stick_Click_R", "Touchpad" };

                    foreach (string side in sides) {
                        w.WriteLine($"### {side} JoyCon ###");
                        foreach (string orient in orientations) {
                            w.WriteLine($"# {orient} Mode");
                            foreach (string btn in buttons) {
                                string key = $"Indep_{orient}_{side}_{btn}";
                                string defaultVal = GetDefaultMapping(key);
                                w.WriteLine($"{key} = {defaultVal}");
                            }
                            w.WriteLine("");
                        }
                    }

                    w.WriteLine("### Joined JoyCons ###");
                    foreach (string side in sides) {
                        w.WriteLine($"# {side} Side");
                        foreach (string btn in buttons) {
                            string key = $"Joined_{side}_{btn}";
                            string defaultVal = GetDefaultMapping(key);
                            w.WriteLine($"{key} = {defaultVal}");
                        }
                        w.WriteLine("");
                    }

                    w.WriteLine("### Pro Controller ###");
                    foreach (string btn in buttons) {
                        string key = $"Pro_{btn}";
                        string defaultVal = GetDefaultMapping(key);
                        w.WriteLine($"{key} = {defaultVal}");
                    }
                    w.WriteLine("");

                    w.WriteLine("### Gyroscope ###");
                    w.WriteLine("# Multipliers for Gyro-to-Stick/Mouse calculation");
                    w.WriteLine("Gyro_Sensitivity_X = 1.0");
                    w.WriteLine("Gyro_Sensitivity_Y = 1.0");
                    w.WriteLine("");
                    w.WriteLine("# Orientation for single JoyCons: Horizontal, Vertical");
                    w.WriteLine("# If not set, uses global setting from App.config");
                    w.WriteLine("# Gyro_Orientation_Left = Vertical");
                    w.WriteLine("# Gyro_Orientation_Right = Vertical");
                }
            } catch { }
        }

        private static string GetDefaultMapping(string key) {
            // This is just to populate the default file with sane values
            bool swapAB = Boolean.Parse(System.Configuration.ConfigurationManager.AppSettings["SwapAB"]);
            bool swapXY = Boolean.Parse(System.Configuration.ConfigurationManager.AppSettings["SwapXY"]);

            if (key.Contains("Joined") || key.Contains("Pro")) {
                bool isPro = key.Contains("Pro");
                bool isRight = key.Contains("Right") || isPro;
                bool isLeft = key.Contains("Left") || isPro;

                // Face Buttons
                if (key.EndsWith("Face_A")) {
                    if (!isRight) return "NONE";
                    return !swapAB ? "B" : "A";
                }
                if (key.EndsWith("Face_B")) {
                    if (!isRight) return "NONE";
                    return !swapAB ? "A" : "B";
                }
                if (key.EndsWith("Face_X")) {
                    if (!isRight) return "NONE";
                    return !swapXY ? "Y" : "X";
                }
                if (key.EndsWith("Face_Y")) {
                    if (!isRight) return "NONE";
                    return !swapXY ? "X" : "Y";
                }

                // Dpad
                if (key.EndsWith("Dpad_Up")) {
                    if (!isLeft) return "NONE";
                    return "DPAD_UP";
                }
                if (key.EndsWith("Dpad_Down")) {
                    if (!isLeft) return "NONE";
                    return "DPAD_DOWN";
                }
                if (key.EndsWith("Dpad_Left")) {
                    if (!isLeft) return "NONE";
                    return "DPAD_LEFT";
                }
                if (key.EndsWith("Dpad_Right")) {
                    if (!isLeft) return "NONE";
                    return "DPAD_RIGHT";
                }

                // Shoulders & Triggers
                if (key.EndsWith("Shoulder_L")) {
                    if (!isLeft) return "NONE";
                    return "SHOULDER_1";
                }
                if (key.EndsWith("Shoulder_R")) {
                    if (!isRight) return "NONE";
                    return "SHOULDER_1";
                }
                if (key.EndsWith("Trigger_L")) {
                    if (!isLeft) return "NONE";
                    return "SHOULDER_2";
                }
                if (key.EndsWith("Trigger_R")) {
                    if (!isRight) return "NONE";
                    return "SHOULDER_2";
                }

                // System & Stick
                if (key.EndsWith("Back")) {
                    if (!isLeft) return "NONE";
                    return "MINUS";
                }
                if (key.EndsWith("Start")) {
                    if (!isRight) return "NONE";
                    return "PLUS";
                }
                if (key.EndsWith("Guide")) {
                    if (!isRight) return "NONE";
                    return "HOME";
                }
                if (key.EndsWith("Touchpad")) {
                    if (!isLeft) return "NONE";
                    return "CAPTURE";
                }

                if (key.EndsWith("Stick_Click_L")) {
                    if (!isLeft) return "NONE";
                    return "STICK";
                }
                if (key.EndsWith("Stick_Click_R")) {
                    if (!isRight) return "NONE";
                    return "STICK";
                }
            }

            if (key.Contains("Vertical_Left")) {
                if (key.EndsWith("Face_A")) return "DPAD_DOWN";
                if (key.EndsWith("Face_B")) return "DPAD_RIGHT";
                if (key.EndsWith("Face_X")) return "DPAD_LEFT";
                if (key.EndsWith("Face_Y")) return "DPAD_UP";
                if (key.EndsWith("Shoulder_L")) return "SL";
                if (key.EndsWith("Shoulder_R")) return "SHOULDER_1";
                if (key.EndsWith("Trigger_L")) return "SR";
                if (key.EndsWith("Trigger_R")) return "SHOULDER_2";
                if (key.EndsWith("Back")) return "CAPTURE"; 
                if (key.EndsWith("Start")) return "MINUS";
                if (key.EndsWith("Stick_Click_L")) return "STICK";
            }
            if (key.Contains("Vertical_Right")) {
                if (key.EndsWith("Face_A")) return "B";
                if (key.EndsWith("Face_B")) return "A";
                if (key.EndsWith("Face_X")) return "Y";
                if (key.EndsWith("Face_Y")) return "X";
                if (key.EndsWith("Shoulder_L")) return "SL";
                if (key.EndsWith("Shoulder_R")) return "SHOULDER_1";
                if (key.EndsWith("Trigger_L")) return "SR";
                if (key.EndsWith("Trigger_R")) return "SHOULDER_2";
                if (key.EndsWith("Back")) return "HOME";
                if (key.EndsWith("Start")) return "PLUS";
                if (key.EndsWith("Stick_Click_L")) return "STICK";
            }
            // For Horizontal, use standard sideways logic (Rail Up)
            if (key.Contains("Horizontal")) {
                bool isLeft = key.Contains("Left");
                if (isLeft) {
                    if (key.EndsWith("Face_A")) return "DPAD_LEFT";  // Bottom
                    if (key.EndsWith("Face_B")) return "DPAD_DOWN";  // Right
                    if (key.EndsWith("Face_X")) return "DPAD_UP";    // Left
                    if (key.EndsWith("Face_Y")) return "DPAD_RIGHT"; // Top
                    if (key.EndsWith("Back")) return "CAPTURE";
                    if (key.EndsWith("Start")) return "MINUS";
                } else {
                    if (key.EndsWith("Face_A")) return "A"; // Bottom
                    if (key.EndsWith("Face_B")) return "X"; // Right
                    if (key.EndsWith("Face_X")) return "B"; // Left
                    if (key.EndsWith("Face_Y")) return "Y"; // Top
                    if (key.EndsWith("Back")) return "HOME";
                    if (key.EndsWith("Start")) return "PLUS";
                }
                if (key.EndsWith("Shoulder_L")) return "SL";
                if (key.EndsWith("Shoulder_R")) return "SR";
                if (key.EndsWith("Trigger_L")) return "SHOULDER_2";
                if (key.EndsWith("Trigger_R")) return "SHOULDER_1";
                if (key.EndsWith("Stick_Click_L")) return "STICK";
            }
            return "NONE";
        }

        public static Joycon.Button GetButton(string key) {
            if (map.ContainsKey(key)) return map[key];
            return Joycon.Button.NONE;
        }

        public static float GetFloat(string key) {
            if (floatMap.ContainsKey(key)) return floatMap[key];
            if (key.Contains("Sensitivity")) return 1.0f;
            return 0.0f;
        }

        public static string GetString(string key) {
            if (stringMap.ContainsKey(key)) return stringMap[key];
            return null;
        }
    }
}
