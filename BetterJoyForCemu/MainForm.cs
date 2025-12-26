using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Runtime.InteropServices;

namespace BetterJoyForCemu {
    public partial class MainForm : Form {
        public bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
        public List<Button> con, loc;
        public bool calibrate;
        private Timer countDown;
        private int count;
        public bool shakeInputEnabled = Boolean.Parse(ConfigurationManager.AppSettings["EnableShakeInput"]);
        public float shakeSesitivity = float.Parse(ConfigurationManager.AppSettings["ShakeInputSensitivity"]);
        public float shakeDelay = float.Parse(ConfigurationManager.AppSettings["ShakeInputDelay"]);
        public Joycon selectedJoycon = null;
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] 
        static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")]
        public static extern bool ChangeWindowMessageFilter(uint msg, uint dwFlag);
        private const uint MSGFLT_ADD = 1;
        private int WM_SHOWME;


        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_SHOWME) {
                ShowFromTray();
            }
            base.WndProc(ref m);
        }

        public enum NonOriginalController : int {
            Disabled = 0,
            DefaultCalibration = 1,
            ControllerCalibration = 2,
        }

        public MainForm() {
            WM_SHOWME = (int)RegisterWindowMessage("BetterJoy_Show");
            ChangeWindowMessageFilter((uint)WM_SHOWME, MSGFLT_ADD);

            InitializeComponent();

            if (!allowCalibration)
                AutoCalibrate.Hide();

            con = new List<Button> { con1, con2, con3, con4 };

            foreach(Button b in con) {
                b.Paint += new PaintEventHandler(conBtn_Paint);
            }

            loc = new List<Button> { loc1, loc2, loc3, loc4 };

            //list all options
            string[] myConfigs = ConfigurationManager.AppSettings.AllKeys;
            Size childSize = new Size(150, 20);
            for (int i = 0; i != myConfigs.Length; i++) {
                settingsTable.RowCount++;
                settingsTable.Controls.Add(new Label() { Text = myConfigs[i], TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, Size = childSize }, 0, i);

                var value = ConfigurationManager.AppSettings[myConfigs[i]];
                Control childControl;
                if (value == "true" || value == "false") {
                    childControl = new CheckBox() { Checked = Boolean.Parse(value), Size = childSize };
                } else {
                    childControl = new TextBox() { Text = value, Size = childSize };
                }

                childControl.MouseClick += cbBox_Changed;
                settingsTable.Controls.Add(childControl, 1, i);
            }
        }

        private void conBtn_Paint(object sender, PaintEventArgs e) {
            Button btn = (Button)sender;
            if (btn.Tag != null && btn.Tag is Joycon) {
                Joycon j = (Joycon)btn.Tag;
                
                // Solo dibujar si tenemos un voltaje válido (> 0)
                if (j.BatteryVoltage > 0) {
                    string voltText = string.Format("{0:0.00}V", j.BatteryVoltage);
                    
                    using (Font font = new Font("Arial", 8, FontStyle.Bold)) {
                        SizeF textSize = e.Graphics.MeasureString(voltText, font);
                        PointF location = new PointF(btn.Width - textSize.Width - 2, btn.Height - textSize.Height - 2);
                        e.Graphics.DrawString(voltText, font, Brushes.Black, location.X + 1, location.Y + 1);
                        e.Graphics.DrawString(voltText, font, Brushes.White, location);
                    }
                }
            }
        }

        private void HideToTray() {
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.BalloonTipText = "Double click the tray icon to maximise!";
            notifyIcon.ShowBalloonTip(0);
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void ShowFromTray() {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Icon = Properties.Resources.betterjoyforcemu_icon;
            notifyIcon.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e) {
            if (this.WindowState == FormWindowState.Minimized) {
                HideToTray();
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
            ShowFromTray();
        }

        private void MainForm_Load(object sender, EventArgs e) {
            Mappings.form = this;
            Config.Init();
            Mappings.Load();

            Program.Start();

            passiveScanBox.Checked = Config.IntValue("ProgressiveScan") == 1;
            startInTrayBox.Checked = Config.IntValue("StartInTray") == 1;

            if (Config.IntValue("StartInTray") == 1) {
                HideToTray();
            } else {
                ShowFromTray();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            try {
                Program.Stop();
                Environment.Exit(0);
            } catch { }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e) { // this does not work, for some reason. Fix before release
            try {
                Program.Stop();
                Close();
                Environment.Exit(0);
            } catch { }
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            donationLink.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        private void passiveScanBox_CheckedChanged(object sender, EventArgs e) {
            Config.SetValue("ProgressiveScan", passiveScanBox.Checked ? "1" : "0");
            Config.Save();
        }

        public void AppendTextBox(string value) { // https://stackoverflow.com/questions/519233/writing-to-a-textbox-from-another-thread
            if (InvokeRequired) {
                this.Invoke(new Action<string>(AppendTextBox), new object[] { value });
                return;
            }
            console.AppendText(value);
        }

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);
        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public async void locBtnClickAsync(object sender, MouseEventArgs e) {
            Button bb = sender as Button;

            if (bb.Tag.GetType() == typeof(Button)) {
                Button button = bb.Tag as Button;

                if (button.Tag.GetType() == typeof(Joycon)) {
                    Joycon v = (Joycon)button.Tag;
                    v.SetRumble(160.0f, 320.0f, 1.0f);
                    await Task.Delay(300);
                    v.SetRumble(160.0f, 320.0f, 0);
                }
            }
        }

        public void conBtnClick(object sender, MouseEventArgs e) {
            Button button = sender as Button;

            if (button.Tag.GetType() == typeof(Joycon)) {
                Joycon v = (Joycon)button.Tag;

                // --- LÓGICA CLICK DERECHO (Emparejar o Forzar Gyro) ---
                if (e.Button == MouseButtons.Right) {
                    if (!v.isPro && v.other == null) {
                        v.UpdateVoltage();
                        // Si ya está en modo Vertical (por click izquierdo) o ya tiene el gyro forzado, buscamos pareja
                        if (v.isVerticalMode) {
                            bool succ = false;
                            foreach (Joycon jc in Program.mgr.j) {
                                // Buscamos pareja que también esté en un estado Vertical (isVerticalMode o forceGyroVertical)
                                if (!jc.isPro && jc.isLeft != v.isLeft && jc != v && jc.other == null && (jc.isVerticalMode || jc.forceGyroVertical)) {
                                    v.other = jc;
                                    jc.other = v;
                                    v.isVerticalMode = false; 
                                    jc.isVerticalMode = false;
                                    v.forceGyroVertical = false; 
                                    jc.forceGyroVertical = false;
                                    // Siempre el Joy-Con DERECHO pierde su controller virtual
                                    // El Joy-Con IZQUIERDO será el "master" que produce la salida
                                    Joycon rightJoy = v.isLeft ? jc : v;
                                    if (rightJoy.out_xbox != null) { rightJoy.out_xbox.Disconnect(); rightJoy.out_xbox = null; }
                                    if (rightJoy.out_ds4 != null) { rightJoy.out_ds4.Disconnect(); rightJoy.out_ds4 = null; }

                                    foreach (Button b in con) {
                                        if (b.Tag == jc) b.BackgroundImage = jc.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
                                        if (b.Tag == v) b.BackgroundImage = v.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
                                    }
                                    succ = true;
                                    AppendTextBox("JoyCons emparejados exitosamente.\r\n");
                                    break;
                                }
                            }
                            if (!succ) {
                                AppendTextBox("No se encontró pareja en modo Vertical.\r\n");
                            }
                        } else if (!v.isVerticalMode) {
                            // Si está en Horizontal, el click derecho switchea solo el gyro (Solo Gyro)
                            v.forceGyroVertical = !v.forceGyroVertical;
                            AppendTextBox("JoyCon " + v.PadId + ": Gyro forzado a " + (v.forceGyroVertical ? "Vertical" : "Horizontal") + ".\r\n");
                        }
                    }
                    return;
                }

                // --- LÓGICA CLICK IZQUIERDO (Toggle Modo / Separar) ---
                if (e.Button == MouseButtons.Left) {
                    if (!v.isPro) {
                        v.UpdateVoltage();
                        if (v.other != null) {

                            // --- INICIO CAMBIO: Borrar de la memoria al separar manualmente ---
                            string ignored;
                            Program.mgr.joinedConnectionCache.TryRemove(v.serial_number, out ignored);
                            Program.mgr.joinedConnectionCache.TryRemove(v.other.serial_number, out ignored);
                            // --- FIN CAMBIO ---

                            // Separar mandos unidos
                            Joycon other = v.other;
                            ReenableViGEm(v);
                            if (other != v) ReenableViGEm(other);

                            foreach (Button b in con) {
                                if (b.Tag == v || (other != v && b.Tag == other)) {
                                    Joycon curr = (Joycon)b.Tag;
                                    b.BackgroundImage = curr.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;
                                    curr.isVerticalMode = false;
                                    curr.forceGyroVertical = false;
                                    if (curr != v) curr.other = null;
                                }
                            }
                            v.other = null;
                            AppendTextBox("JoyCons separados y reseteados a Horizontal.\r\n");
                        } else {
                            // Alternar entre modo Horizontal y Vertical estándar (Cambiando el icono)
                            v.isVerticalMode = !v.isVerticalMode;
                            v.forceGyroVertical = false; // El modo vertical completo anula el forzado de gyro
                            
                            button.BackgroundImage = v.isVerticalMode ? 
                                (v.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right) : 
                                (v.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s);
                                
                            AppendTextBox("JoyCon " + v.PadId + ": Modo " + (v.isVerticalMode ? "Vertical" : "Horizontal") + ".\r\n");
                        }
                        Mappings.Load();
                    }
                }
                if (e.Button == MouseButtons.Middle) {
                    // Verificar si la calibración está habilitada en config
                    if (!allowCalibration) {
                        AppendTextBox("Calibration disabled in settings.\r\n");
                        return;
                    }

                    // Verificar si ya se está calibrando algo
                    if (this.calibrate || (countDown != null && countDown.Enabled)) {
                        AppendTextBox("Calibration already in progress.\r\n");
                        return;
                    }

                    // Seleccionar este mando específico
                    selectedJoycon = v;
                    AppendTextBox($"Starting calibration for JoyCon {v.PadId + 1} via middle-click...\r\n");

                    // Iniciar proceso (usando el método que creamos en la respuesta anterior)
                    RunCalibration();
                }
            }
        }

        private void startInTrayBox_CheckedChanged(object sender, EventArgs e) {
            Config.SetValue("StartInTray", startInTrayBox.Checked ? "1" : "0");
            Config.Save();
        }

        private void btn_open3rdP_Click(object sender, EventArgs e) {
            _3rdPartyControllers partyForm = new _3rdPartyControllers();
            partyForm.ShowDialog();
        }

        private void settingsApply_Click(object sender, EventArgs e) {
            var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            var settings = configFile.AppSettings.Settings;

            for (int row = 0; row < ConfigurationManager.AppSettings.AllKeys.Length; row++) {
                var valCtl = settingsTable.GetControlFromPosition(1, row);
                var KeyCtl = settingsTable.GetControlFromPosition(0, row).Text;

                if (valCtl.GetType() == typeof(CheckBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }
            }

            try {
                configFile.Save(ConfigurationSaveMode.Modified);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings.\r\n");
            }

            ConfigurationManager.AppSettings["AutoPowerOff"] = "false";  // Prevent joycons poweroff when applying settings
            Application.Restart();
            Environment.Exit(0);
        }

        void ReenableViGEm(Joycon v) {
            if (showAsXInput && v.out_xbox == null) {
                v.out_xbox = new Controller.OutputControllerXbox360();

                if (toRumble)
                    v.out_xbox.FeedbackReceived += v.ReceiveRumble;
                v.out_xbox.Connect();
            }

            if (showAsDS4 && v.out_ds4 == null) {
                v.out_ds4 = new Controller.OutputControllerDualShock4();

                if (toRumble)
                    v.out_ds4.FeedbackReceived += v.Ds4_FeedbackReceived;
                v.out_ds4.Connect();
            }
        }

        private void foldLbl_Click(object sender, EventArgs e) {
            rightPanel.Visible = !rightPanel.Visible;
            foldLbl.Text = rightPanel.Visible ? "<" : ">";
        }

        private void cbBox_Changed(object sender, EventArgs e) {
            var coord = settingsTable.GetPositionFromControl(sender as Control);

            var valCtl = settingsTable.GetControlFromPosition(coord.Column, coord.Row);
            var KeyCtl = settingsTable.GetControlFromPosition(coord.Column - 1, coord.Row).Text;

            try {
                var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                var settings = configFile.AppSettings.Settings;
                if (valCtl.GetType() == typeof(CheckBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }

                if (KeyCtl == "HomeLEDOn") {
                    bool on = settings[KeyCtl].Value.ToLower() == "true";
                    foreach (Joycon j in Program.mgr.j) {
                        j.SetHomeLight(on);
                    }
                }

                configFile.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings\r\n");
                Trace.WriteLine(String.Format("rw {0}, column {1}, {2}, {3}", coord.Row, coord.Column, sender.GetType(), KeyCtl));
            }
        }
        private void StartCalibrate(object sender, EventArgs e) {
            if (Program.mgr.j.Count == 0) {
                this.console.Text = "Please connect a controller.";
                return;
            }

            // Crea el menú contextual
            ContextMenuStrip cm = new ContextMenuStrip();

            // OPCIÓN 1: Calibrar Todos
            cm.Items.Add("All Connected Controllers", null, (s, ev) => {
                selectedJoycon = null; // null indicará "Modo Todos"
                RunCalibration();
            });

            // Separador visual
            cm.Items.Add(new ToolStripSeparator());

            // OPCIÓN 2: Calibrar Individuales
            foreach (Joycon j in Program.mgr.j) {
                string name = String.Format("Joycon {0} ({1})", j.PadId + 1, j.isPro ? "Pro" : "Joy");
                cm.Items.Add(name, null, (s, ev) => {
                    selectedJoycon = j; // Asignamos el mando específico
                    RunCalibration();
                });
            }

            // Mostrar el menú debajo del botón
            cm.Show(AutoCalibrate, new Point(0, AutoCalibrate.Height));
        }

        private void RunCalibration() {
            this.AutoCalibrate.Enabled = false;
            countDown = new Timer();
            // INICIO CAMBIO: Reducir el tiempo de cuenta regresiva a 1 segundo
            this.count = 1; 
            // FIN CAMBIO
            this.CountDown(null, null); // Llama inmediatamente para mostrar el mensaje
            countDown.Tick += new EventHandler(CountDown);
            countDown.Interval = 1000;
            countDown.Enabled = true;
        }



        private void StartGetData() {
            // 1. Determinar qué calibrar
            List<Joycon> targets = new List<Joycon>();

            if (selectedJoycon != null) {
                targets.Add(selectedJoycon);
            } else {
                // Si es null, añadimos TODOS
                targets.AddRange(Program.mgr.j);
            }

            // 2. Iniciar calibración concurrente (Async lógicamente)
            // Cada Joycon empezará a llenar su buffer en su propio ciclo de polling USB
            foreach (var j in targets) {
                j.BeginCalibration();
            }

            this.console.Text += $"Collecting data for {targets.Count} controller(s)...\r\n";

            // 3. Iniciar temporizador de espera (1 segundo)
            countDown = new Timer();
            this.count = 3; // Usamos 0 para que el próximo tick sea el final
            this.calibrate = true;
            countDown.Tick += new EventHandler(CalcData);
            countDown.Interval = 1000; // Esperar 1 segundo acumulando datos
            countDown.Enabled = true;
        }

        private void btn_reassign_open_Click(object sender, EventArgs e) {
            Reassign mapForm = new Reassign();
            mapForm.ShowDialog();
        }

        private void CountDown(object sender, EventArgs e) {
            // Este método ahora se llama una vez, y el siguiente tick (si ocurriera) 
            // ya no ejecutaría el contenido del if (this.count == 0) porque lo detendríamos.
            
            // INICIO CAMBIO: Detener el timer después de la primera ejecución
            if (this.count == 0) {
                this.console.Text = "Calibrating...";
                countDown.Stop();
                countDown.Dispose(); // Liberar recursos del timer
                this.StartGetData();
            } else {
                // Mensaje inicial (solo se muestra si count > 0)
                this.console.Text = "Plese keep the controller flat." + "\r\n";
                this.console.Text += "Calibration will start in " + this.count + " second."; // Cambiado a 'second' para 1s
                this.count--;
                // Si el count llegó a 0 justo ahora, la próxima vez (en el tick de arriba) 
                // ejecutará el if(this.count == 0).
            }
        }

        private void CalcData(object sender, EventArgs e) {
            if (this.count == 0) {
                // Detener timer
                countDown.Stop();
                countDown.Dispose();
                this.calibrate = false;

                // Determinar objetivos nuevamente
                List<Joycon> targets = new List<Joycon>();
                if (selectedJoycon != null) targets.Add(selectedJoycon);
                else targets.AddRange(Program.mgr.j);

                if (targets.Count == 0) {
                    this.AutoCalibrate.Enabled = true;
                    return;
                }

                int successCount = 0;

                // Procesar resultados
                foreach (var j in targets) {
                    // Obtener resultado procesado internamente por el Joycon
                    // Esto detiene el flag IsCalibrating internamente
                    float[] result = j.EndCalibrationAndGetMedian();

                    if (result != null) {
                        // 1. Guardar en disco (Persistencia)
                        CalibrationManager.UpdateCalibration(j.serial_number, result);

                        // 2. Aplicar en memoria (Hot reload)
                        j.active_calibration_data = result;

                        successCount++;
                        this.console.Text += $"Saved calibration for {j.serial_number}\r\n";
                    } else {
                        this.console.Text += $"Failed to calibrate {j.serial_number} (No data)\r\n";
                    }
                }

                if (successCount > 0)
                    this.console.Text += "Concurrent calibration finished.\r\n";

                this.AutoCalibrate.Enabled = true;
                this.selectedJoycon = null;
            } else {
                this.console.Text += ".";
                this.count--;
            }
        }
    }
}
