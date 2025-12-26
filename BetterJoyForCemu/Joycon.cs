using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using BetterJoyForCemu.Controller;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    public class Joycon {
        public string path = String.Empty;
        // Variable para almacenar el color (Por defecto gris oscuro si falla la lectura)
        public System.Drawing.Color BodyColor = System.Drawing.Color.FromArgb(255, 82, 82, 82);
        public bool isPro = false;
        public bool isSnes = false;
        public bool is64 = false;
        public bool isVerticalMode = false;
        public bool forceGyroVertical = false;
        public float BatteryVoltage = 0f; // Variable para almacenar el voltaje
        bool isUSB = false;
        private Joycon _other = null;

        // 64 vars
        float maxX = 0.5f;
        float minX = -0.5f;
        float maxY = 0.5f;
        float minY = -0.5f;

        public Joycon other {
            get {
                return _other;
            }
            set {
                _other = value;

                // If the other Joycon is itself, the Joycon is sideways
                if (_other == null || _other == this) {
                    // Set LED to current Pad ID
                    SetLEDByPlayerNum(PadId);
                } else {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    SetLEDByPlayerNum(lowestPadId);
                }

                // Refresh settings when orientation changes
                this.swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
                this.swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);

                if (form != null) {
                    form.Invoke(new MethodInvoker(delegate {
                        BatteryChanged();
                    }));
                }
            }
        }
        public bool active_gyro = false;
        private long gyroToggleBtnTimestamp = -1; // Marca de tiempo de cuando se empezó a presionar
        private bool gyroToggleHasFired = false;  // Evita que cambie 60 veces por segundo mientras mantienes presionado

        private long inactivity = Stopwatch.GetTimestamp();

        public bool send = true;

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);
        //public DebugType debug_type = DebugType.NONE; //Keep this for manual debugging during development.
        public bool isLeft;
        private float velX_smoothed = 0f;
        private float velY_smoothed = 0f;
        private float angleX_fused = 0f; // Ángulo X (Yaw) acumulado
        private float angleY_fused = 0f; // Ángulo Y (Pitch) fusionado con acelerómetro

        // Configuración del filtro (Ajustable)
        private float alpha = 0.05f;      // Suavizado del movimiento (0.01 - 0.10)
        private float compFilter = 0.90f; // Cuánto confiamos en el Gyro vs Acelerómetro (0.95 - 0.99)  

        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;
        public enum Button : int {
            NONE = -1,
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };
        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];
        private long[] buttons_down_timestamp = new long[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        private IntPtr handle;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone;
        private UInt16[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone2;
        private UInt16[] stick2_precal = { 0, 0 };

        private bool stop_polling = true;
        private bool imu_enabled = false;
        private Int16[] acc_r = { 0, 0, 0 };
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g;

        private Int16[] gyr_r = { 0, 0, 0 };
        private Int16[] gyr_neutral = { 0, 0, 0 };
        private Int16[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g;

        private float[] cur_rotation; // Filtered IMU data

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private struct Rumble {
            public Queue<float[]> queue;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                if (queue.Count > 15) {
                    queue.Dequeue();
                }
                queue.Enqueue(rumbleQueue);
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                float[] queued_data = queue.Dequeue();

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private long lastRumbleTime = 0;
        private float[] lastRumbleData = new float[3]; 


        private byte global_count = 0;
        private string debug_str;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;
        ushort ds4_ts = 0;
        ulong lag;

        int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public MainForm form;

        public bool IsCalibrating { get; private set; } = false;
        private List<short> _calBufGx = new List<short>();
        private List<short> _calBufGy = new List<short>();
        private List<short> _calBufGz = new List<short>();
        private List<short> _calBufAx = new List<short>();
        private List<short> _calBufAy = new List<short>();
        private List<short> _calBufAz = new List<short>();

        public void BeginCalibration() {
            _calBufGx.Clear(); _calBufGy.Clear(); _calBufGz.Clear();
            _calBufAx.Clear(); _calBufAy.Clear(); _calBufAz.Clear();
            IsCalibrating = true;
        }

        public float[] EndCalibrationAndGetMedian() {
            IsCalibrating = false;

            if (_calBufGx.Count < 10) return null;

            float[] res = new float[6];

            res[0] = _calBufGx.OrderBy(x => x).ElementAt(_calBufGx.Count / 2);
            res[1] = _calBufGy.OrderBy(x => x).ElementAt(_calBufGy.Count / 2);
            res[2] = _calBufGz.OrderBy(x => x).ElementAt(_calBufGz.Count / 2);
            res[3] = _calBufAx.OrderBy(x => x).ElementAt(_calBufAx.Count / 2);
            res[4] = _calBufAy.OrderBy(x => x).ElementAt(_calBufAy.Count / 2);

            float medianZ = _calBufAz.OrderBy(x => x).ElementAt(_calBufAz.Count / 2);
            if (this.isLeft) res[5] = medianZ - 4010;
            else res[5] = medianZ + 4010;

            return res;
        }

        public byte LED { get; private set; } = 0x0;
        public void SetLEDByPlayerNum(int id) {
            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["UseIncrementalLights"].Value.ToLower() == "true") {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        public string serial_number;
        bool thirdParty = false;

        public float[] active_calibration_data = new float[6] { 0, 0, 0, 0, 0, 0 };
        static float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        private MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false, bool is64 = false, bool thirdParty = false) {
            serial_number = serialNum;
            active_calibration_data = new float[6];
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes || is64;
            this.isSnes = isSnes;
            this.is64 = is64;
            isUSB = serialNum == "000000000001";
            this.thirdParty = thirdParty;

            this.path = path;

            connection = isUSB ? 0x01 : 0x02;

            if (showAsXInput) {
                out_xbox = new OutputControllerXbox360();
                if (toRumble)
                    out_xbox.FeedbackReceived += ReceiveRumble;
            }

            if (showAsDS4) {
                out_ds4 = new OutputControllerDualShock4();
                if (toRumble)
                    out_ds4.FeedbackReceived += Ds4_FeedbackReceived;
            }

            // Initialize orientation mode from Mappings or global config
            string orientationKey = "Gyro_Orientation_" + (isLeft ? "Left" : "Right");
            string orientationSetting = Mappings.GetString(orientationKey);
            if (orientationSetting != null) {
                isVerticalMode = (orientationSetting.ToLower() == "vertical");
            } else {
                isVerticalMode = false; // Default to horizontal to match UI
            }

            // INICIO CAMBIO: Cargar calibración persistente
            float[] cachedCali = CalibrationManager.GetCalibration(this.serial_number);
            if (cachedCali != null) {
                this.active_calibration_data = cachedCali;
                Console.WriteLine($"Loaded cached calibration for {this.serial_number}");
            }
            // FIN CAMBIO
        }

        public void getActiveData() {
            float[] cachedCali = CalibrationManager.GetCalibration(this.serial_number);
            if (cachedCali != null) {
                this.active_calibration_data = cachedCali;
            }
        }

        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox(s + "\r\n");
            }
        }
        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }
        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }
        public int Attach() {
            state = state_.ATTACHED;

            // Make sure command is received
            HIDapi.hid_set_nonblocking(handle, 0);

            byte[] a = { 0x0 };

            // Connect
            if (isUSB) {
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                form.AppendTextBox("Using USB.\r\n");

                a[0] = 0x80;
                a[1] = 0x1;
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                if (a[0] != 0x81) { // can occur when USB connection isn't closed properly
                    form.AppendTextBox("Resetting USB connection.\r\n");
                    Subcommand(0x06, new byte[] { 0x01 }, 1);
                    throw new Exception("reset_usb");
                }

                if (a[3] == 0x3) {
                    PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
                }

                // USB Pairing
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                a[0] = 0x80; a[1] = 0x2; // Handshake
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x3; // 3Mbit baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x2; // Handshake at new baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x4; // Prevent HID timeout
                HIDapi.hid_write(handle, a, new UIntPtr(2)); // doesn't actually prevent timout...
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

            }
            dump_calibration_data();

            // Bluetooth manual pairing
            byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1);
            Subcommand(0x48, new byte[] { 0x01 }, 1);

            Subcommand(0x3, new byte[] { 0x30 }, 1);
            DebugPrint("Done with init.", DebugType.COMMS);

            UpdateVoltage();

            HIDapi.hid_set_nonblocking(handle, 1);
            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void SetPlayerLEDByPadID() {
            SetLEDByPlayerNum(PadId);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public void SetHomeLight(bool on) {
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                a[0] = 0x1F;
                a[1] = 0xF0;
            } else {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state) {
            byte[] a = { state };
            Subcommand(0x06, a, 1);
        }

        public void PowerOff() {
            if (state > state_.DROPPED) {
                HIDapi.hid_set_nonblocking(handle, 0);
                SetHCIState(0x00);
                state = state_.DROPPED;
            }
        }

        private void BatteryChanged() { // battery changed level
            System.Drawing.Color colorToUse = this.BodyColor;
            if (other != null && other != this) {
                // Usar el color del "maestro" (el que tiene el PadId menor) del grupo
                if (other.PadId < this.PadId) {
                    colorToUse = other.BodyColor;
                }
            }

            foreach (var v in form.con) {
                if (v.Tag == this) {
                    // Si la batería es crítica, mantenemos el rojo para avisar al usuario
                    // El valor -1 indica que aún no se ha leído la batería, por lo que usamos el color del cuerpo
                    if (battery != -1 && battery <= 0) {
                        v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
                    } else {
                        // Usamos el color determinado (el propio o el del maestro si están unidos)
                        // Usamos 0xAA (170) en Alpha para mantener la transparencia original del diseño
                        v.BackColor = System.Drawing.Color.FromArgb(0xAA, colorToUse);
                    }
                }
            }

            if (battery <= 1) {
                form.notifyIcon.Visible = true;
                form.notifyIcon.BalloonTipText = String.Format("Controller {0} ({1}) - low battery notification!", PadId, isPro ? "Pro Controller" : (isSnes ? "SNES Controller" : (is64? "N64 Controller" : (isLeft ? "Joycon Left" : "Joycon Right"))));
                form.notifyIcon.ShowBalloonTip(0);
            }
        }

        public void UpdateVoltage() {
            if (state < state_.ATTACHED) return;

            new Thread(() => {
                try {
                    // Pide voltaje
                    byte[] response = Subcommand(0x50, new byte[] { 0 }, 1, false);

                    if (response != null && response.Length >= 17) {
                        int mV = response[15] | (response[16] << 8);
                        
                        // --- DEBUG: Imprimir lo que lee el mando ---
                        string debugMsg = String.Format("JoyCon {0} Raw Voltage: {1}mV", PadId, mV);
                        form.AppendTextBox(debugMsg + "\r\n");
                        // -------------------------------------------

                        if (mV > 2800 && mV < 5500) { 
                            this.BatteryVoltage = mV / 1000.0f;
                            
                            if (form != null) {
                                form.Invoke(new MethodInvoker(delegate {
                                    form.Refresh();
                                }));
                            }
                        } 
                        // --- DEBUG: Avisar si el rango lo descarta ---
                        else {
                            form.AppendTextBox(String.Format("JoyCon {0} Voltage {1}mV OUT OF RANGE (Ignored)\r\n", PadId, mV));
                        }
                        // ---------------------------------------------
                    }
                } catch (Exception ex) { 
                    form.AppendTextBox("Error reading voltage: " + ex.Message + "\r\n");
                }
            }).Start();
        }

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        public void Detach(bool close = false) {
            stop_polling = true;
            stop_output = true;

            this.BatteryVoltage = 0f; // Reset battery voltage

            if (out_xbox != null) {
                out_xbox.Disconnect();
            }

            if (out_ds4 != null) {
                out_ds4.Disconnect();
            }

            if (state > state_.NO_JOYCONS) {
                HIDapi.hid_set_nonblocking(handle, 0);

                // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
                //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

                if (isUSB) {
                    byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
                    a[0] = 0x80; a[1] = 0x5; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                    a[0] = 0x80; a[1] = 0x6; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                }
            }
            if (close || state > state_.DROPPED) {
                HIDapi.hid_close(handle);
            }
            state = state_.NOT_ATTACHED;
        }

        private byte ts_en;
        private int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;
            byte[] raw_buf = new byte[report_len];
            int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5);

            if (ret > 0) {
                // Arrays para guardar las 3 muestras de este paquete
                float[] batchGyroZ = new float[3];
                float[] batchGyroY = new float[3];

                for (int n = 0; n < 3; n++) {
                    ExtractIMUValues(raw_buf, n);
                    this.cur_rotation = AHRS.GetEulerAngles(); // Mantenemos esto para otras funciones

                    // Guardamos los datos CRUDOS (más rápidos que Filtered para apuntar)
                    // Si prefieres Filtered (menos temblor, más lag), usa cur_rotation aquí.
                    batchGyroZ[n] = gyr_g.Z; 
                    batchGyroY[n] = gyr_g.Y;

                    // ... (código original de timestamps y botones - NO TOCAR) ...
                    byte lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0) {
                        Timestamp += (ulong)lag * 5000;
                        ProcessButtonsAndStick(raw_buf);
                        DoThingsWithButtons();
                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery) BatteryChanged();
                    }
                    Timestamp += 5000;
                    packetCounter++;
                    
                    // Server UDP (Dolphin) necesita actualización por cada muestra (n)
                    if (Program.server != null) Program.server.NewReportIncoming(this);
                }

                // --- AQUÍ ESTÁ EL CAMBIO ---
                // Procesamos el movimiento UNA VEZ por paquete USB, usando el promedio de las 3 muestras.
                // dt = 0.005f (5ms), pasamos eso para calcular la física.
                ProcessGyroAccumulation(batchGyroZ, batchGyroY, 0.005f);

                /*
                // Enviamos a Xbox/DS4 UNA VEZ por paquete USB (cada 15ms)
                // Esto elimina el tartamudeo (judder) provocado por enviar datos demasiado rápido al driver.
                if (out_xbox != null) {
                    try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch { }
                }
                if (out_ds4 != null) {
                    try { out_ds4.UpdateInput(MapToDualShock4Input(this)); } catch { }
                }
                */

                if (ts_en == raw_buf[1] && !(isSnes || is64)) {
                    // ... logs originales ...
                }
                ts_en = raw_buf[1];
            }
            return ret;
        }

        private readonly Stopwatch shakeTimer = Stopwatch.StartNew(); //Setup a timer for measuring shake in milliseconds
        private long shakedTime = 0;
        private bool hasShaked;
        void DetectShake() {
            if (form.shakeInputEnabled) {
                long currentShakeTime = shakeTimer.ElapsedMilliseconds;

                // Shake detection logic
                bool isShaking = GetAccel().LengthSquared() >= form.shakeSesitivity;
                if (isShaking && currentShakeTime >= shakedTime + form.shakeDelay || isShaking && shakedTime == 0) {
                    shakedTime = currentShakeTime;
                    hasShaked = true;

                    // Mapped shake key down
                    Simulate(Config.Value("shake"), false, false);
                    DebugPrint("Shaked at time: " + shakedTime.ToString(), DebugType.SHAKE);
                }

                // If controller was shaked then release mapped key after a small delay to simulate a button press, then reset hasShaked
                if (hasShaked && currentShakeTime >= shakedTime + 10) {
                    // Mapped shake key up
                    Simulate(Config.Value("shake"), false, true);
                    DebugPrint("Shake completed", DebugType.SHAKE);
                    hasShaked = false;
                }

            } else {
                shakeTimer.Stop();
                return;
            }
        }

        bool dragToggle = Boolean.Parse(ConfigurationManager.AppSettings["DragToggle"]);
        Dictionary<int, bool> mouse_toggle_btn = new Dictionary<int, bool>();
        private void Simulate(string s, bool click = true, bool up = false) {
            if (s.StartsWith("key_")) {
                WindowsInput.Events.KeyCode key = (WindowsInput.Events.KeyCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(key).Invoke();
                } else {
                    if (up) {
                        WindowsInput.Simulate.Events().Release(key).Invoke();
                    } else {
                        WindowsInput.Simulate.Events().Hold(key).Invoke();
                    }
                }
            } else if (s.StartsWith("mse_")) {
                WindowsInput.Events.ButtonCode button = (WindowsInput.Events.ButtonCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(button).Invoke();
                } else {
                    if (dragToggle) {
                        if (!up) {
                            bool release;
                            mouse_toggle_btn.TryGetValue((int)button, out release);
                            if (release)
                                WindowsInput.Simulate.Events().Release(button).Invoke();
                            else
                                WindowsInput.Simulate.Events().Hold(button).Invoke();
                            mouse_toggle_btn[(int)button] = !release;
                        }
                    } else {
                        if (up) {
                            WindowsInput.Simulate.Events().Release(button).Invoke();
                        } else {
                            WindowsInput.Simulate.Events().Hold(button).Invoke();
                        }
                    }
                }
            }
        }

        private void ProcessGyroAccumulation(float[] gyroZ_samples, float[] gyroY_samples, float dt) {
            string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
            bool isGyroActive = (Config.Value("active_gyro") == "0" || active_gyro);

            if (extraGyroFeature.Substring(0, 3) != "joy" || !isGyroActive) {
                return;
            }

            // 1. Promediar las muestras del paquete (Elimina vibración y saltos)
            float avgGyroZ = 0f;
            float avgGyroY = 0f;
            int samples = gyroZ_samples.Length; // Deberían ser 3

            for (int i = 0; i < samples; i++) {
                avgGyroZ += gyroZ_samples[i];
                avgGyroY += gyroY_samples[i];
            }
            avgGyroZ /= samples;
            avgGyroY /= samples;

            // 2. Zona Muerta de Entrada (Evita el Drifting minúsculo)
            if (Math.Abs(avgGyroZ) < 0.5f) avgGyroZ = 0f;
            if (Math.Abs(avgGyroY) < 0.5f) avgGyroY = 0f;

            // 3. Obtener Sensibilidad del Config
            float sensX = Mappings.GetFloat("Gyro_Sensitivity_X") * float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
            float sensY = Mappings.GetFloat("Gyro_Sensitivity_Y") * float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);

            // 4. FACTOR DE REDUCCIÓN DE SENSIBILIDAD (CRUCIAL)
            float reductionFactor = 0.0001f;

            // 5. Acumular Posición (Integración)
            // Usamos variables temporales para calcular el destino
            float tempX = targetStickX + (avgGyroZ * sensX * reductionFactor * (dt * samples * 1000));
            float tempY = targetStickY - (avgGyroY * sensY * reductionFactor * (dt * samples * 1000));

            // 6. Clamp (Límites)
            targetStickX = Math.Max(-1.0f, Math.Min(1.0f, tempX));
            targetStickY = Math.Max(-1.0f, Math.Min(1.0f, tempY));
        }

        // For Joystick->Joystick inputs
        private void SimulateContinous(int origin, string s) {
            if (s.StartsWith("joy_")) {
                int button = Int32.Parse(s.Substring(4));
                buttons[button] |= buttons[origin];
            }
        }

        bool HomeLongPowerOff = Boolean.Parse(ConfigurationManager.AppSettings["HomeLongPowerOff"]);
        long PowerOffInactivityMins = Int32.Parse(ConfigurationManager.AppSettings["PowerOffInactivity"]);

        bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        long lastDoubleClick = -1;

        string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
        bool UseFilteredIMU = Boolean.Parse(ConfigurationManager.AppSettings["UseFilteredIMU"]);
        int GyroMouseSensitivityX = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityX"]);
        int GyroMouseSensitivityY = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityY"]);
        float GyroStickSensitivityX = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
        float GyroStickSensitivityY = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);
        float GyroStickReduction = float.Parse(ConfigurationManager.AppSettings["GyroStickReduction"]);
        bool GyroHoldToggle = Boolean.Parse(ConfigurationManager.AppSettings["GyroHoldToggle"]);
        bool GyroAnalogSliders = Boolean.Parse(ConfigurationManager.AppSettings["GyroAnalogSliders"]);
        int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        static bool SingleJoyConGyroVertical = Boolean.Parse(ConfigurationManager.AppSettings["SingleJoyConGyroVertical"]);
        static bool LeftJoyConForceABXY = Boolean.Parse(ConfigurationManager.AppSettings["LeftJoyConForceABXY"]);


        byte[] sliderVal = new byte[] { 0, 0 };


        private void DoThingsWithButtons() {
            int powerOffButton = (int)((isPro || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            // --- Lógica de apagado por inactividad (Código original) ---
            if (HomeLongPowerOff && buttons[powerOffButton]) {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0) {
                    if (other != null) other.PowerOff();
                    PowerOff();
                    return;
                }
            }

            // --- Lógica de doble click para orientación (Código original) ---
            if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1 && !isPro) {
                if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                    form.conBtnClick(form.con[PadId], new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                    return;
                }
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && !isPro) {
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            }

            if (PowerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > PowerOffInactivityMins * 60 * 1000) {
                    if (other != null) other.PowerOff();
                    PowerOff();
                    return;
                }
            }

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE]) Simulate(Config.Value("capture"));
            if (buttons_down[(int)Button.HOME]) Simulate(Config.Value("home"));
            SimulateContinous((int)Button.CAPTURE, Config.Value("capture"));
            SimulateContinous((int)Button.HOME, Config.Value("home"));

            // --- Lógica de botones SL/SR (Código original) ---
            if (isLeft) {
                if (buttons_down[(int)Button.SL]) Simulate(Config.Value("sl_l"), false, false);
                if (buttons_up[(int)Button.SL]) Simulate(Config.Value("sl_l"), false, true);
                if (buttons_down[(int)Button.SR]) Simulate(Config.Value("sr_l"), false, false);
                if (buttons_up[(int)Button.SR]) Simulate(Config.Value("sr_l"), false, true);
                SimulateContinous((int)Button.SL, Config.Value("sl_l"));
                SimulateContinous((int)Button.SR, Config.Value("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL]) Simulate(Config.Value("sl_r"), false, false);
                if (buttons_up[(int)Button.SL]) Simulate(Config.Value("sl_r"), false, true);
                if (buttons_down[(int)Button.SR]) Simulate(Config.Value("sr_r"), false, false);
                if (buttons_up[(int)Button.SR]) Simulate(Config.Value("sr_r"), false, true);
                SimulateContinous((int)Button.SL, Config.Value("sl_r"));
                SimulateContinous((int)Button.SR, Config.Value("sr_r"));
            }

            this.cur_rotation = AHRS.GetEulerAngles();
            float dt = 0.015f; 

            // --- Lógica de Sliders Analógicos (Código original) ---
            if (GyroAnalogSliders && (other != null || isPro)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Joycon left = isLeft ? this : (isPro ? this : this.other); Joycon right = !isLeft ? this : (isPro ? this : this.other);

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            // ==============================================================================
            // === NUEVA LÓGICA DE ACTIVACIÓN DE GIROSCOPIO (2 SEGUNDOS HOLD + RESET) ===
            // ==============================================================================
            string res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("joy_")) {
                int i = Int32.Parse(res_val.Substring(4));
                
                bool isPressed = buttons[i] || (other != null && !SingleJoyConGyroVertical && other.buttons[i]);

                if (isPressed) {
                    long currentTimestamp = Stopwatch.GetTimestamp();

                    if (gyroToggleBtnTimestamp == -1) {
                        gyroToggleBtnTimestamp = currentTimestamp;
                        gyroToggleHasFired = false;
                    }

                    double elapsedMs = (currentTimestamp - gyroToggleBtnTimestamp) / 10000.0;

                    // Si pasaron 2 segundos y no se ha disparado el evento
                    if (elapsedMs >= 2000.0 && !gyroToggleHasFired) {
                        active_gyro = !active_gyro; 
                        gyroToggleHasFired = true; 

                        // SI SE DESACTIVA: Reseteamos la posición a 0 inmediatamente
                        if (!active_gyro) {
                            targetStickX = 0f; targetStickY = 0f;
                            currentStickX = 0f; currentStickY = 0f;
                            
                            // También reseteamos el stick físico para asegurar que no quede pegado
                            stick[0] = 0f; stick[1] = 0f;
                            if (isPro || other != null) { stick2[0] = 0f; stick2[1] = 0f; }
                        }

                        form.AppendTextBox("Gyro Switch: " + (active_gyro ? "ACTIVADO" : "DESACTIVADO") + "\r\n");
                        
                        if (active_gyro) SetRumble(160, 320, 0.6f); 
                        else SetRumble(160, 160, 0.2f);
                    }
                } else {
                    gyroToggleBtnTimestamp = -1;
                    gyroToggleHasFired = false;
                }
            }

            // ==============================================================================
            // === LÓGICA DE CENTRADO (MOUSE Y JOYSTICK) ===
            // ==============================================================================
            if (extraGyroFeature == "mouse" && (isPro || (other == null) || (other != null && (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]) ? isLeft : !isLeft)))) {
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    int dx, dy;
                    if (UseFilteredIMU) {
                        dx = (int)(GyroMouseSensitivityX * (cur_rotation[1] - cur_rotation[4])); 
                        dy = (int)-(GyroMouseSensitivityY * (cur_rotation[0] - cur_rotation[3])); 
                    } else {
                        dx = (int)(GyroMouseSensitivityX * (gyr_g.Z * dt));
                        dy = (int)-(GyroMouseSensitivityY * (gyr_g.Y * dt));
                    }
                    dx = (int)(dx * Mappings.GetFloat("Gyro_Sensitivity_X"));
                    dy = (int)(dy * Mappings.GetFloat("Gyro_Sensitivity_Y"));
                    WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
                }
            }

            // Botón de Reset / Recentrar
            res_val = Config.Value("reset_mouse");
            if (res_val.StartsWith("joy_")) {
                int i = Int32.Parse(res_val.Substring(4));
                if (buttons_down[i] || (other != null && other.buttons_down[i])) {
                    // 1. Resetear Mouse (Original)
                    WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
                    
                    // 2. Resetear Joystick Interpolado (NUEVO)
                    targetStickX = 0f;
                    targetStickY = 0f;
                    currentStickX = 0f;
                    currentStickY = 0f;
                    
                    // 3. Resetear Joystick físico
                    if (extraGyroFeature == "joy_left") { stick[0] = 0; stick[1] = 0; }
                    else { stick2[0] = 0; stick2[1] = 0; }
                }
            }
        }

        private Thread PollThreadObj;
        private Thread OutputThreadObj;
        private volatile bool stop_output = false;

        // Variables para la interpolación del Gyro
        private float targetStickX = 0f;
        private float targetStickY = 0f;
        private float currentStickX = 0f;
        private float currentStickY = 0f;

        // Factor de suavizado para 250Hz (4ms). 
        // Un valor de 0.1f a 0.2f suele dar buena suavidad sin lag perceptible.
        private float outputInterpolationFactor = 0.15f;
        private void OutputLoop() {
            // Intentamos correr a 250Hz aprox (cada 4ms)
            int interval = 4;

            while (!stop_output) {
                if (state > state_.DROPPED) {

                    // 1. INTERPOLACIÓN (La magia)
                    // Movemos el stick actual un pequeño paso hacia el stick objetivo
                    currentStickX += (targetStickX - currentStickX) * outputInterpolationFactor;
                    currentStickY += (targetStickY - currentStickY) * outputInterpolationFactor;

                    // 2. ACTUALIZAR SALIDA XBOX / DS4
                    // Nota: MapToXbox360Input usa 'stick' y 'stick2'. 
                    // Necesitamos asegurarnos de que MapToXbox360Input use nuestros valores interpolados
                    // para el stick que tenga el gyro asignado.

                    // Un pequeño hack temporal: Sobrescribimos el stick físico con el interpolado 
                    // justo antes de mapear, si el gyro está activo.
                    if (Config.Value("active_gyro") == "0" || active_gyro) {
                        string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
                        if (extraGyroFeature.StartsWith("joy")) {
                            float[] control_stick = (extraGyroFeature == "joy_left") ? stick : stick2;

                            // Guardamos el valor original por si acaso (opcional)
                            // float oldX = control_stick[0]; float oldY = control_stick[1];

                            // Aplicamos el valor interpolado suave
                            control_stick[0] = Math.Max(-1.0f, Math.Min(1.0f, currentStickX));
                            control_stick[1] = Math.Max(-1.0f, Math.Min(1.0f, currentStickY));
                        }
                    }

                    // Enviar al driver virtual
                    if (out_xbox != null) {
                        try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch { }
                    }
                    if (out_ds4 != null) {
                        try { out_ds4.UpdateInput(MapToDualShock4Input(this)); } catch { }
                    }
                }

                Thread.Sleep(interval);
            }
        }

        private void Poll() {
            stop_polling = false;
            int attempts = 0;
            while (!stop_polling & state > state_.NO_JOYCONS) {
                if (rumble_obj.queue.Count > 0) {
                    SendRumble(rumble_obj.GetData());
                }
                int a = ReceiveRaw();

                if (a > 0 && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                    break;
                } else if (a < 0) {
                    // An error on read.
                    //form.AppendTextBox("Pause 5ms");
                    Thread.Sleep((Int32)5);
                    ++attempts;
                } else if (a == 0) {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }
            }
        }

        public float[] otherStick = { 0, 0 };

        bool swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
        bool swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);
        bool realn64Range = Boolean.Parse(ConfigurationManager.AppSettings["N64Range"]);
        float stickScalingFactor = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]);
        float stickScalingFactor2 = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]);

        private int ProcessButtonsAndStick(byte[] report_buf) {
            if (report_buf[0] == 0x00) throw new ArgumentException("received undefined report. This is probably a bug");
            if (!isSnes) {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (isPro) {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                stick = CenterSticks(stick_precal, stick_cal, deadzone, isLeft ? stickScalingFactor : stickScalingFactor2);

                if (isPro) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2, stickScalingFactor2);
                }

                // Stick swapping is now handled in the mapping functions (MapToXbox360Input / MapToDualShock4Input)
                // to ensure clarity and support for different controller masters.
            }
            //

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];

                buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
                buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (isPro) {
                    buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                } else if (!isLeft) { // For Right Joycon, we need to read face buttons from its report section (shared with Pro)
                    buttons[(int)Button.B] = (report_buf[3] & 0x04) != 0;
                    buttons[(int)Button.A] = (report_buf[3] & 0x08) != 0;
                    buttons[(int)Button.X] = (report_buf[3] & 0x02) != 0;
                    buttons[(int)Button.Y] = (report_buf[3] & 0x01) != 0;
                }



                if (isLeft && other != null && other != this) {
                    buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                    buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
                }

                if (!isLeft && other != null && other != this) {
                    buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                }

                long timestamp = Stopwatch.GetTimestamp();

                lock (buttons_up) {
                    lock (buttons_down) {
                        bool changed = false;
                        for (int i = 0; i < buttons.Length; ++i) {
                            buttons_up[i] = (down_[i] & !buttons[i]);
                            buttons_down[i] = (!down_[i] & buttons[i]);
                            if (down_[i] != buttons[i])
                                buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                            if (buttons_up[i] || buttons_down[i])
                                changed = true;
                        }

                        inactivity = (changed) ? timestamp : inactivity;
                    }
                }
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (!(isSnes || is64)) {
                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                if (this.IsCalibrating) {
                    _calBufGx.Add(gyr_r[0]);
                    _calBufGy.Add(gyr_r[1]);
                    _calBufGz.Add(gyr_r[2]);
                    _calBufAx.Add(acc_r[0]);
                    _calBufAy.Add(acc_r[1]);
                    _calBufAz.Add(acc_r[2]);
                }

                if (form.allowCalibration) {
                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - active_calibration_data[3]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.X = (gyr_r[i] - active_calibration_data[0]) * (816.0f / gyr_sen[i]);
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - active_calibration_data[4]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - active_calibration_data[1]) * (816.0f / gyr_sen[i]);
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - active_calibration_data[5]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - active_calibration_data[2]) * (816.0f / gyr_sen[i]);
                                break;
                        }
                    }
                } else {
                    Int16[] offset;
                    if (isPro)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                if (other == null && !isPro && !isVerticalMode && !forceGyroVertical) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        public void Begin() {
            if (PollThreadObj == null) {
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                stop_output = false;
                OutputThreadObj = new Thread(new ThreadStart(OutputLoop));
                OutputThreadObj.IsBackground = true;
                OutputThreadObj.Priority = ThreadPriority.AboveNormal; // Importante para suavidad
                OutputThreadObj.Start();

                form.AppendTextBox("Starting poll and output threads.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        // Should really be called calculating stick data
        private float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz, float scaling_factor) {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);

            if (scaling_factor != 1.0f) {
                s[0] *= scaling_factor;
                s[1] *= scaling_factor;

                s[0] = Math.Max(Math.Min(s[0], 1.0f), -1.0f);
                s[1] = Math.Max(Math.Min(s[1], 1.0f), -1.0f);
            }

            return s;
        }

        private static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= Joycon.state_.ATTACHED) return;

            // Optimización: No enviar si los datos son iguales a los anteriores
            if (Math.Abs(lastRumbleData[0] - low_freq) < 0.1f && 
                Math.Abs(lastRumbleData[1] - high_freq) < 0.1f && 
                Math.Abs(lastRumbleData[2] - amp) < 0.01f) {
                return;
            }

            // Optimización: Rate Limiting (ej. máx 1 actualización cada 15ms)
            long now = Stopwatch.GetTimestamp();
            if ((now - lastRumbleTime) / 10000 < 15) return; 

            // Actualizar estado
            lastRumbleData[0] = low_freq;
            lastRumbleData[1] = high_freq;
            lastRumbleData[2] = amp;
            lastRumbleTime = now;

            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        private void SendRumble(byte[] buf) {
            byte[] buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
        }

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            if (print) { PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}"); };
            HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
            int tries = 0;
            do {
                int res = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100);
                if (res < 1) DebugPrint("No response.", DebugType.COMMS);
                else if (print) { PrintArray(response, DebugType.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}"); }
                tries++;
            } while (tries < 10 && response[0] != 0x21 && response[14] != sc);

            return response;
        }

        private void dump_calibration_data() {
            if (isSnes || is64 || thirdParty) {
                short[] temp = (short[])ConfigurationManager.AppSettings["acc_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0]; acc_sensiti[1] = temp[1]; acc_sensiti[2] = temp[2];
                temp = (short[])ConfigurationManager.AppSettings["gyr_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0]; gyr_sensiti[1] = temp[1]; gyr_sensiti[2] = temp[2];
                ushort[] temp2 = (ushort[])ConfigurationManager.AppSettings["stick_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0]; stick_cal[1] = temp2[1]; stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3]; stick_cal[4] = temp2[4]; stick_cal[5] = temp2[5];
                deadzone = ushort.Parse(ConfigurationManager.AppSettings["deadzone"]);
                temp2 = (ushort[])ConfigurationManager.AppSettings["stick2_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0]; stick2_cal[1] = temp2[1]; stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3]; stick2_cal[4] = temp2[4]; stick2_cal[5] = temp2[5];
                deadzone2 = ushort.Parse(ConfigurationManager.AppSettings["deadzone2"]);
                return;
            }

            HIDapi.hid_set_nonblocking(handle, 0);
            byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
            bool found = false;
            for (int i = 0; i < 9; ++i) {
                if (buf_[i] != 0xff) {
                    form.AppendTextBox("Using user stick calibration data.\r\n");
                    found = true;
                    break;
                }
            }
            if (!found) {
                form.AppendTextBox("Using factory stick calibration data.\r\n");
                buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
            }
            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (isPro) {
                buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
                found = false;
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
                }
                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }

            GetBodyColor();

            HIDapi.hid_set_nonblocking(handle, 1);
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1) {
                    break;
                }
            }
            Array.Copy(buf_, 20, read_buf, 0, len);
            if (print) PrintArray(read_buf, DebugType.COMMS, len);
            return read_buf;
        }

        private void GetBodyColor() {
            if (thirdParty)
                return;

            try {
                // Leer 3 bytes desde 0x6050 (Factory body color)
                byte[] color_data = ReadSPI(0x60, 0x50, 3);

                // Verificar que no sea 0xFFFFFF (vacío) o 0x000000
                if ((color_data[0] != 0xFF || color_data[1] != 0xFF || color_data[2] != 0xFF) &&
                    (color_data[0] != 0x00 || color_data[1] != 0x00 || color_data[2] != 0x00)) {
                    this.BodyColor = System.Drawing.Color.FromArgb(255, color_data[0], color_data[1], color_data[2]);
                    DebugPrint($"Joy-Con Color Read: R:{color_data[0]} G:{color_data[1]} B:{color_data[2]}", DebugType.COMMS);

                    form.Invoke(new MethodInvoker(delegate {
                        BatteryChanged();
                        if (other != null && other != this) {
                            other.BatteryChanged();
                        }
                    }));
                }
            } catch (Exception e) {
                DebugPrint("Error reading Joy-Con color: " + e.Message, DebugType.COMMS);
            }
        }

        private void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }


        private static float GetNormalizedValue(float value, float rawMin, float rawMax, float normalizedMin, float normalizedMax)
        {
            return (value - rawMin) / (rawMax - rawMin) * (normalizedMax - normalizedMin) + normalizedMin;
        }

        private static float[] Getn64StickValues(Joycon input)
        {
            var isLeft = input.isLeft;
            var other = input.other;
            var stick = input.stick;
            var stick2 = input.stick2;
            var stick_correction = new float[] { 0f, 0f};

            var xAxis = (other == input && !isLeft) ? stick2[0] : stick[0];
            var yAxis = (other == input && !isLeft) ? stick2[1] : stick[1];


            if (xAxis < input.minX)
            {
                input.minX = xAxis;
            }

            if (xAxis > input.maxX)
            {
                input.maxX = xAxis;
            }

            if (yAxis < input.minY)
            {
                input.minY = yAxis;
            }

            if (yAxis > input.maxY)
            {
                input.maxY = yAxis;
            }

            var middleX = (input.minX + (input.maxX - input.minX)/2);
            var middleY = (input.minY + (input.maxY - input.minY)/2);
            #if DEBUG
            var desc = "";
            desc += "x: "+xAxis+"; y: "+yAxis;
            desc += "\n X: ["+input.minX+", "+input.maxX+"]; Y: ["+input.minY+", "+input.maxY+"] ";
            desc += "; middle ["+middleX+", "+middleY+"]";
                
            Debug.WriteLine(desc);
            #endif

            var negative_normalized = new float[] {-1, 0};
            var positive_normalized = new float[] {0, 1};

            var xRange = new float[] {-1f, 1f};
            var yRange = new float[] {-1f, 1f};

            if (input.realn64Range)
            {
                xRange = new float[] {-0.79f, 0.79f};
                yRange = new float[] {-0.79f, 0.79f};
            }
            

            if (xAxis < (middleX - middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, input.minX, (middleX - middleX), xRange[0], 0f);
            }

            if (xAxis > (middleX+middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, (middleX+middleX), input.maxX, 0f, xRange[1]);
            }

            if (yAxis < (middleY-middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, input.minY, (middleY-middleY), yRange[0], 0f);
            }

            if (yAxis > (middleY+middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, (middleY+middleY), input.maxY, 0f, yRange[1]);
            }


            return stick_correction;
        }

        private static bool GetMap(string key, bool[] buttons) {
            Joycon.Button btn = Mappings.GetButton(key);
            if ((int)btn >= 0 && (int)btn < buttons.Length) return buttons[(int)btn];
            return false;
        }

        public static OutputControllerXbox360InputState MapToXbox360Input(Joycon input) {
            var output = new OutputControllerXbox360InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;
            var forceABXY = Joycon.LeftJoyConForceABXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var other = input.other;
            var GyroStickSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;
            var isVertical = input.isVerticalMode;

            if (is64) {
                // N64 logic remains relatively specialized, but let's at least use Buttons properly
                output.axis_right_x = (short)((buttons[(int)Button.X] ? Int16.MinValue : 0) + (buttons[(int)Button.MINUS] ? Int16.MaxValue : 0));
                output.axis_right_y = (short)((buttons[(int)Button.SHOULDER2_2] ? Int16.MinValue : 0) + (buttons[(int)Button.Y] ? Int16.MaxValue : 0));

                var n64Stick = Getn64StickValues(input);
                output.axis_left_x = CastStickValue(n64Stick[0]);
                output.axis_left_y = CastStickValue(n64Stick[1]);

                output.start = buttons[(int)Button.PLUS];
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);

                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];
                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.guide = buttons[(int)Button.HOME];
            } else if (isPro) {
                string p = "Pro_";
                output.a = GetMap(p + "Face_A", buttons);
                output.b = GetMap(p + "Face_B", buttons);
                output.x = GetMap(p + "Face_X", buttons);
                output.y = GetMap(p + "Face_Y", buttons);

                output.dpad_up = GetMap(p + "Dpad_Up", buttons);
                output.dpad_down = GetMap(p + "Dpad_Down", buttons);
                output.dpad_left = GetMap(p + "Dpad_Left", buttons);
                output.dpad_right = GetMap(p + "Dpad_Right", buttons);

                output.back = GetMap(p + "Back", buttons);
                output.start = GetMap(p + "Start", buttons);
                output.guide = GetMap(p + "Guide", buttons);

                output.shoulder_left = GetMap(p + "Shoulder_L", buttons);
                output.shoulder_right = GetMap(p + "Shoulder_R", buttons);
                output.trigger_left = (byte)(GetMap(p + "Trigger_L", buttons) ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(GetMap(p + "Trigger_R", buttons) ? Byte.MaxValue : 0);

                output.thumb_stick_left = GetMap(p + "Stick_Click_L", buttons);
                output.thumb_stick_right = GetMap(p + "Stick_Click_R", buttons);
            } else {
                if (other != null) {
                    Joycon leftCon = isLeft ? input : other;
                    Joycon rightCon = isLeft ? other : input;

                    string pL = "Joined_Left_";
                    string pR = "Joined_Right_";

                    // Face Buttons (Right JoyCon)
                    output.a = GetMap(pR + "Face_A", rightCon.buttons);
                    output.b = GetMap(pR + "Face_B", rightCon.buttons);
                    output.x = GetMap(pR + "Face_X", rightCon.buttons);
                    output.y = GetMap(pR + "Face_Y", rightCon.buttons);

                    // DPads (Left JoyCon)
                    output.dpad_up = GetMap(pL + "Dpad_Up", leftCon.buttons);
                    output.dpad_down = GetMap(pL + "Dpad_Down", leftCon.buttons);
                    output.dpad_left = GetMap(pL + "Dpad_Left", leftCon.buttons);
                    output.dpad_right = GetMap(pL + "Dpad_Right", leftCon.buttons);

                    // System Buttons
                    output.back = GetMap(pL + "Back", leftCon.buttons);
                    output.start = GetMap(pR + "Start", rightCon.buttons);
                    output.guide = GetMap(pL + "Guide", leftCon.buttons) || GetMap(pR + "Guide", rightCon.buttons);

                    // Shoulders & Triggers
                    output.shoulder_left = GetMap(pL + "Shoulder_L", leftCon.buttons);
                    output.shoulder_right = GetMap(pR + "Shoulder_R", rightCon.buttons);
                    output.trigger_left = (byte)(GetMap(pL + "Trigger_L", leftCon.buttons) ? Byte.MaxValue : 0);
                    output.trigger_right = (byte)(GetMap(pR + "Trigger_R", rightCon.buttons) ? Byte.MaxValue : 0);

                    // Stick Click
                    output.thumb_stick_left = GetMap(pL + "Stick_Click_L", leftCon.buttons);
                    output.thumb_stick_right = GetMap(pR + "Stick_Click_R", rightCon.buttons);

                    // Axis assignment (Sticks)
                    output.axis_left_x = CastStickValue(leftCon.stick[0]);
                    output.axis_left_y = CastStickValue(leftCon.stick[1]);
                    output.axis_right_x = CastStickValue(rightCon.stick[0]);
                    output.axis_right_y = CastStickValue(rightCon.stick[1]);
                }
 else { // single joycon mode
                    string side = isLeft ? "Left" : "Right";
                    string orient = isVertical ? "Vertical" : "Horizontal";
                    string p = $"Indep_{orient}_{side}_";

                    // Face Buttons
                    output.a = GetMap(p + "Face_A", buttons);
                    output.b = GetMap(p + "Face_B", buttons);
                    output.x = GetMap(p + "Face_X", buttons);
                    output.y = GetMap(p + "Face_Y", buttons);


                    // DPads
                    output.dpad_up = GetMap(p + "Dpad_Up", buttons);
                    output.dpad_down = GetMap(p + "Dpad_Down", buttons);
                    output.dpad_left = GetMap(p + "Dpad_Left", buttons);
                    output.dpad_right = GetMap(p + "Dpad_Right", buttons);

                    // Shoulders & Triggers
                    output.shoulder_left = GetMap(p + "Shoulder_L", buttons);
                    output.shoulder_right = GetMap(p + "Shoulder_R", buttons);
                    
                    output.trigger_left = (byte)(GetMap(p + "Trigger_L", buttons) ? Byte.MaxValue : 0);
                    output.trigger_right = (byte)(GetMap(p + "Trigger_R", buttons) ? Byte.MaxValue : 0);

                    // System Buttons
                    output.back = GetMap(p + "Back", buttons);
                    output.start = GetMap(p + "Start", buttons);
                    output.guide = GetMap(p + "Guide", buttons);

                    // Stick Click
                    output.thumb_stick_left = GetMap(p + "Stick_Click_L", buttons);
                    output.thumb_stick_right = GetMap(p + "Stick_Click_R", buttons);
                }
            }

            if (Config.Value("home") != "0")
                output.guide = false;

            if (!(isSnes || is64)) {
                if (isPro || (isVertical && other == null)) {
                    // Modo Independiente Vertical
                    if (isVertical && !isLeft) {
                        output.axis_left_x = CastStickValue(stick[0]);
                        output.axis_left_y = CastStickValue(stick[1]);
                        output.axis_right_x = CastStickValue(stick2[0]);
                        output.axis_right_y = CastStickValue(stick2[1]);
                    }
                    else {
                        // Comportamiento original para Pro o JoyCon Izquierdo Vertical
                        output.axis_left_x = CastStickValue((isVertical && !isLeft) ? stick2[0] : stick[0]);
                        output.axis_left_y = CastStickValue((isVertical && !isLeft) ? stick2[1] : stick[1]);
                        output.axis_right_x = CastStickValue((isVertical && !isLeft) ? stick[0] : stick2[0]);
                        output.axis_right_y = CastStickValue((isVertical && !isLeft) ? stick[1] : stick2[1]);
                    }
                } else if (other == null) { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                    output.axis_right_x = CastStickValue(stick2[0]);
                    output.axis_right_y = CastStickValue(stick2[1]);
                }
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input) {
            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var isVertical = input.isVerticalMode;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;


            if (is64) {
                output.thumb_right_x = (byte)((buttons[(int)Button.X] ? Byte.MinValue : 0) + (buttons[(int)Button.MINUS] ? Byte.MaxValue : 0));
                output.thumb_right_y = (byte)((buttons[(int)Button.SHOULDER2_2] ? Byte.MinValue : 0) + (buttons[(int)Button.Y] ? Byte.MaxValue : 0));

                output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);

                output.options = buttons[(int)Button.PLUS];
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = buttons[(int)Button.SHOULDER_2];
                output.trigger_right = buttons[(int)Button.STICK];
                output.trigger_left_value = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;
            } else if (isPro) {
                string p = "Pro_";
                output.cross = GetMap(p + "Face_A", buttons);
                output.circle = GetMap(p + "Face_B", buttons);
                output.triangle = GetMap(p + "Face_X", buttons);
                output.square = GetMap(p + "Face_Y", buttons);

                bool up = GetMap(p + "Dpad_Up", buttons);
                bool down = GetMap(p + "Dpad_Down", buttons);
                bool left = GetMap(p + "Dpad_Left", buttons);
                bool right = GetMap(p + "Dpad_Right", buttons);

                if (up) {
                    if (left) output.dPad = DpadDirection.Northwest;
                    else if (right) output.dPad = DpadDirection.Northeast;
                    else output.dPad = DpadDirection.North;
                } else if (down) {
                    if (left) output.dPad = DpadDirection.Southwest;
                    else if (right) output.dPad = DpadDirection.Southeast;
                    else output.dPad = DpadDirection.South;
                } else if (left) output.dPad = DpadDirection.West;
                else if (right) output.dPad = DpadDirection.East;
                else output.dPad = DpadDirection.None;

                output.share = GetMap(p + "Touchpad", buttons); // Capture is Touchpad in DS4/Pro context? Verify
                output.options = GetMap(p + "Start", buttons);
                output.ps = GetMap(p + "Guide", buttons);
                output.touchpad = GetMap(p + "Back", buttons); // Minus as Touchpad?
                output.shoulder_left = GetMap(p + "Shoulder_L", buttons);
                output.shoulder_right = GetMap(p + "Shoulder_R", buttons);
                output.thumb_left = GetMap(p + "Stick_Click_L", buttons);
                output.thumb_right = GetMap(p + "Stick_Click_R", buttons);
            } else {
                if (other != null) {
                    Joycon leftCon = isLeft ? input : other;
                    Joycon rightCon = isLeft ? other : input;

                    string pL = "Joined_Left_";
                    string pR = "Joined_Right_";

                    output.cross = GetMap(pR + "Face_A", rightCon.buttons);
                    output.circle = GetMap(pR + "Face_B", rightCon.buttons);
                    output.triangle = GetMap(pR + "Face_X", rightCon.buttons);
                    output.square = GetMap(pR + "Face_Y", rightCon.buttons);

                    bool up = GetMap(pL + "Dpad_Up", leftCon.buttons);
                    bool down = GetMap(pL + "Dpad_Down", leftCon.buttons);
                    bool left = GetMap(pL + "Dpad_Left", leftCon.buttons);
                    bool right = GetMap(pL + "Dpad_Right", leftCon.buttons);

                    if (up) {
                        if (left) output.dPad = DpadDirection.Northwest;
                        else if (right) output.dPad = DpadDirection.Northeast;
                        else output.dPad = DpadDirection.North;
                    } else if (down) {
                        if (left) output.dPad = DpadDirection.Southwest;
                        else if (right) output.dPad = DpadDirection.Southeast;
                        else output.dPad = DpadDirection.South;
                    } else if (left) output.dPad = DpadDirection.West;
                    else if (right) output.dPad = DpadDirection.East;
                    else output.dPad = DpadDirection.None;

                    output.share = isLeft ? GetMap(pL + "Touchpad", leftCon.buttons) : GetMap(pR + "Touchpad", rightCon.buttons);
                    output.options = GetMap(pR + "Start", rightCon.buttons);
                    output.ps = GetMap(pL + "Guide", leftCon.buttons) || GetMap(pR + "Guide", rightCon.buttons);
                    output.touchpad = GetMap(pL + "Back", leftCon.buttons);
                    output.shoulder_left = GetMap(pL + "Shoulder_L", leftCon.buttons);
                    output.shoulder_right = GetMap(pR + "Shoulder_R", rightCon.buttons);
                    output.thumb_left = GetMap(pL + "Stick_Click_L", leftCon.buttons);
                    output.thumb_right = GetMap(pR + "Stick_Click_R", rightCon.buttons);

                    // Axis assignment (Sticks)
                    output.thumb_left_x = CastStickValueByte(-leftCon.stick[0]);
                    output.thumb_left_y = CastStickValueByte(leftCon.stick[1]);
                    output.thumb_right_x = CastStickValueByte(-rightCon.stick[0]);
                    output.thumb_right_y = CastStickValueByte(rightCon.stick[1]);
                }
 else { // single joycon mode
                    string side = isLeft ? "Left" : "Right";
                    string orient = isVertical ? "Vertical" : "Horizontal";
                    string p = $"Indep_{orient}_{side}_";

                    output.cross = GetMap(p + "Face_A", buttons);
                    output.circle = GetMap(p + "Face_B", buttons);
                    output.square = GetMap(p + "Face_X", buttons);
                    output.triangle = GetMap(p + "Face_Y", buttons);

                    output.ps = GetMap(p + "Guide", buttons);
                    output.options = GetMap(p + "Start", buttons);
                    output.share = GetMap(p + "Back", buttons);
                    output.touchpad = GetMap(p + "Touchpad", buttons);

                    output.shoulder_left = GetMap(p + "Shoulder_L", buttons);
                    output.shoulder_right = GetMap(p + "Shoulder_R", buttons);
                    
                    output.thumb_left = GetMap(p + "Stick_Click_L", buttons);
                    output.thumb_right = GetMap(p + "Stick_Click_R", buttons);

                    // DPads (DS4 uses enum for Dpad)
                    bool up = GetMap(p + "Dpad_Up", buttons);
                    bool down = GetMap(p + "Dpad_Down", buttons);
                    bool left = GetMap(p + "Dpad_Left", buttons);
                    bool right = GetMap(p + "Dpad_Right", buttons);

                    if (up) {
                        if (left) output.dPad = DpadDirection.Northwest;
                        else if (right) output.dPad = DpadDirection.Northeast;
                        else output.dPad = DpadDirection.North;
                    } else if (down) {
                        if (left) output.dPad = DpadDirection.Southwest;
                        else if (right) output.dPad = DpadDirection.Southeast;
                        else output.dPad = DpadDirection.South;
                    } else if (left) output.dPad = DpadDirection.West;
                    else if (right) output.dPad = DpadDirection.East;
                    else output.dPad = DpadDirection.None;
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.ps = false;

            if (!(isSnes || is64)) {
                if (isPro || (isVertical && other == null)) {
                    if (isVertical && !isLeft) {
                        output.thumb_left_x = CastStickValueByte(-stick[0]);
                        output.thumb_left_y = CastStickValueByte(stick[1]);
                        
                        output.thumb_right_x = CastStickValueByte(-stick2[0]);
                        output.thumb_right_y = CastStickValueByte(stick2[1]);
                    } else {
                        output.thumb_left_x = CastStickValueByte((isVertical && !isLeft) ? -stick2[0] : -stick[0]);
                        output.thumb_left_y = CastStickValueByte((isVertical && !isLeft) ? stick2[1] : stick[1]);
                        output.thumb_right_x = CastStickValueByte((isVertical && !isLeft) ? -stick[0] : -stick2[0]);
                        output.thumb_right_y = CastStickValueByte((isVertical && !isLeft) ? stick[1] : stick2[1]);
                    }
                } else if (other == null) { 
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (other != null || isPro || isVertical) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    string side = isLeft ? "Left" : "Right";
                    string orient = isVertical ? "Vertical" : "Horizontal";
                    string p = $"Indep_{orient}_{side}_";

                    output.trigger_left_value = (byte)(GetMap(p + "Trigger_L", buttons) ? Byte.MaxValue : 0);
                    output.trigger_right_value = (byte)(GetMap(p + "Trigger_R", buttons) ? Byte.MaxValue : 0);
                }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;
            }

            return output;
        }
    }
}
