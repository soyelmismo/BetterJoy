using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterJoyForCemu {
    public static class CalibrationManager {
        // Ruta del archivo de calibración
        private static readonly string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JoyconCalibration.txt");
        
        // Diccionario en memoria: Serial Number -> Array de 6 floats (Gyros y Accels)
        public static Dictionary<string, float[]> CalibrationCache = new Dictionary<string, float[]>();

        // Carga los datos del archivo al iniciar
        public static void Load() {
            CalibrationCache.Clear();
            if (!File.Exists(path)) return;

            try {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines) {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    var parts = line.Split('|');
                    if (parts.Length == 2) {
                        string serial = parts[0].Trim();
                        string[] valuesObj = parts[1].Split(',');
                        
                        if (valuesObj.Length == 6) {
                            float[] data = new float[6];
                            for (int i = 0; i < 6; i++) {
                                float.TryParse(valuesObj[i], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out data[i]);
                            }
                            if (!CalibrationCache.ContainsKey(serial)) {
                                CalibrationCache.Add(serial, data);
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error loading calibration: {ex.Message}");
            }
        }

        // Guarda el diccionario actual en el archivo
        public static void Save() {
            try {
                using (StreamWriter sw = new StreamWriter(path)) {
                    sw.WriteLine("# Format: SerialNumber|GyroX,GyroY,GyroZ,AccelX,AccelY,AccelZ");
                    foreach (var kvp in CalibrationCache) {
                        string dataStr = string.Join(",", kvp.Value.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        sw.WriteLine($"{kvp.Key}|{dataStr}");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error saving calibration: {ex.Message}");
            }
        }

        // Método helper para actualizar o añadir un mando
        public static void UpdateCalibration(string serial, float[] data) {
            if (CalibrationCache.ContainsKey(serial)) {
                CalibrationCache[serial] = data;
            } else {
                CalibrationCache.Add(serial, data);
            }
            Save(); // Guardar inmediatamente tras actualizar
        }

        // Método para obtener datos (devuelve null si no existe)
        public static float[] GetCalibration(string serial) {
            if (CalibrationCache.ContainsKey(serial)) {
                return CalibrationCache[serial];
            }
            return null; // O devolver un array por defecto si prefieres
        }
    }
}
