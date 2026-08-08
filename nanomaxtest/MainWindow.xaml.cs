using Microsoft.Win32;
using nanomaxtest.Controllers;
using nanomaxtest.Engines;
using nanomaxtest.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace nanomaxtest
{
    public partial class MainWindow : Window
    {
        private DeviceController _deviceController = new DeviceController();
        private MacroEngine _engine = new MacroEngine();
        private nanomaxtest.Managers.FileManager _fileManager = new nanomaxtest.Managers.FileManager();
        private nanomaxtest.Managers.TaskSequenceManager _sequenceManager;
        private DispatcherTimer _uiTimer;
        private nanomaxtest.Managers.ArrayPresetManager _presetManager;

        private double[] _timeRemaining = new double[3];
        private bool[] _moveCommanded = new bool[3];
        private double[] _targetPos = new double[3];
        private double[] _targetVel = new double[3];
        private double _unitMultiplier = 1.0;
        private string _unitString = "mm";
        private Dictionary<TextBox, string> _lastValidValues = new Dictionary<TextBox, string>();

        private decimal?[,] _waypoints = new decimal?[5, 3];
        private List<string> _actionLog = new List<string>();

        private bool _isRecording = false;
        private List<MacroCommand> _recordedPath = new List<MacroCommand>();
        private double[] _lastRecordedPos = new double[3];
        private DateTime[] _lastRecordedTime = new DateTime[3];
        private double[] _plannedDist = new double[3];
        private double[] _plannedVel = new double[3];
        private bool[] _isInterrupted = new bool[3];
        private bool[] _isDirectMoving = new bool[3];
        private DateTime[] _targetEndTime = new DateTime[3];
        private bool[] _hasTargetTime = new bool[3];
        private DateTime[] _targetIssuedAt = new DateTime[3];
        private bool[] _seenMovingAfterCommand = new bool[3];
        private bool _isFaulted = false;
        private readonly object _recordLock = new object();
        private bool _activePollingMode = false;

        private string _appDataPath;

        private string _presetFilePath;
        private string _settingsFilePath;

        public MainWindow()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            InitializeComponent();

            _appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NanoMaxController");
            if (!Directory.Exists(_appDataPath)) Directory.CreateDirectory(_appDataPath);
            _presetFilePath = Path.Combine(_appDataPath, "NanoMax_ArrayPresets.txt");
            _settingsFilePath = Path.Combine(_appDataPath, "NanoMax_Settings.txt");

            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(250);
            _uiTimer.Tick += UiTimer_Tick;

            _actionLog.Add("시간,축,동작,거리(또는 목표위치),속도");

            _sequenceManager = new nanomaxtest.Managers.TaskSequenceManager(_deviceController, _engine);
            _sequenceManager.NotificationRequested += SendNotification;
            _deviceController.SafetyLog = msg => LogAction("SafetyGate", msg, "-", "-");
            dgMacro.ItemsSource = _sequenceManager.MacroSequence;


            _sequenceManager.ArrayPoints.Add(new ArrayPoint { AxisPos = 0, ZPos = 0 });
            dgArrayPoints.ItemsSource = _sequenceManager.ArrayPoints;

            _presetManager = new nanomaxtest.Managers.ArrayPresetManager(_appDataPath);

            WireUpAxisControl(ctrlX, 0);
            WireUpAxisControl(ctrlY, 1);
            WireUpAxisControl(ctrlZ, 2);

            UpdateUnitLabels();
            LoadPresets();
            LoadSettings();
        }

        private void WireUpAxisControl(AxisControl ctrl, int axisIndex)
        {
            ctrl.inTarget.PreviewTextInput += TextBox_PreviewTextInput;
            ctrl.inTarget.GotFocus += TextBox_GotFocus;
            ctrl.inTarget.LostFocus += TextBox_LostFocus;
            ctrl.inTarget.KeyDown += TextBox_KeyDown_Execute;

            ctrl.inVel.PreviewTextInput += TextBox_PreviewTextInput;
            ctrl.inVel.GotFocus += TextBox_GotFocus;
            ctrl.inVel.LostFocus += TextBox_LostFocus;
            ctrl.inVel.KeyDown += TextBox_KeyDown_Execute;

            ctrl.inAcc.PreviewTextInput += TextBox_PreviewTextInput;
            ctrl.inAcc.GotFocus += TextBox_GotFocus;
            ctrl.inAcc.LostFocus += TextBox_LostFocus;
            ctrl.inAcc.KeyDown += TextBox_KeyDown_Execute;

            ctrl.btnHome.Click += (s, e) => _ = RunHomeAxis(axisIndex);
            ctrl.btnStop.Click += (s, e) => StopAxis(axisIndex);
            ctrl.btnPlus.Click += (s, e) => ExecuteMoveRelative(axisIndex, 1.0, ctrl.inTarget, ctrl.inVel, ctrl.inAcc);
            ctrl.btnMinus.Click += (s, e) => ExecuteMoveRelative(axisIndex, -1.0, ctrl.inTarget, ctrl.inVel, ctrl.inAcc);
            ctrl.btnGo.Click += (s, e) => ExecuteMoveAbsolute(axisIndex, ctrl.inTarget, ctrl.inVel, ctrl.inAcc);
            ctrl.cmbMode.MouseRightButtonUp += (s, e) => { ctrl.cmbMode.SelectedIndex = ctrl.cmbMode.SelectedIndex == 0 ? 1 : 0; e.Handled = true; };

            ctrl.sliderJog.ValueChanged += (s, e) => HandleJog(axisIndex, ctrl.sliderJog);
            ctrl.sliderJog.PreviewMouseLeftButtonUp += (s, e) => StopJog(axisIndex, ctrl.sliderJog);
        }

        private void LoadSettings()
        {
            try
            {
                bool loaded = false;
                if (File.Exists(_settingsFilePath))
                {
                    string[] lines = File.ReadAllLines(_settingsFilePath, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("WebhookUrl="))
                        {
                            string val = line.Substring("WebhookUrl=".Length).Trim();
                            if (!string.IsNullOrEmpty(val))
                            {
                                txtWebhookUrl.Text = val;
                                loaded = true;
                            }
                        }
                    }
                }

                if (!loaded)
                {
                    txtWebhookUrl.Text = "";
                }
            }
            catch
            {
                txtWebhookUrl.Text = "";
            }
        }

        private void SaveSettings()
        {
            try
            {
                string url = txtWebhookUrl.Text.Trim();
                File.WriteAllLines(_settingsFilePath, new[] { $"WebhookUrl={url}" }, Encoding.UTF8);
            }
            catch { }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            EnterFault(nameof(CurrentDomain_UnhandledException), e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        }

        private void EnterFault(string where, Exception ex)
        {
            if (_isFaulted) return;
            _isFaulted = true;

            try
            {
                _sequenceManager?.StopAll();
                for (int i = 0; i < 3; i++) _deviceController?.StopProfiled(i);
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                btnConnect.IsEnabled = false;
                btnStartMacro.IsEnabled = false;
                btnStartArray.IsEnabled = false;
                txtGlobalStatus.Text = "FAULT 상태";
                txtGlobalStatus.Foreground = Brushes.Red;
                MessageBox.Show($"{where} 예외로 안전 정지되었습니다.\n{ex.Message}", "FAULT", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private readonly object _logLock = new object();
        private void LogAction(string axis, string action, string distance, string velocity)

        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int currentCount = 0;
            lock (_logLock)
            {
                _actionLog.Add($"{time},{axis},{action},{distance},{velocity}");
                currentCount = _actionLog.Count - 1;
            }
            Dispatcher.InvokeAsync(() => { if (txtLogCount != null) txtLogCount.Text = $"현재 기록된 로그 수: {currentCount}개"; });
        }

        private static readonly HttpClient _httpClient = new HttpClient();

        private async void SendNotification(string message)
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();

                if (chkMacroWebhook?.IsChecked == true)
                {
                    string webhookUrl = txtWebhookUrl.Text.Trim();
                    if (!string.IsNullOrEmpty(webhookUrl))
                    {
                        string escaped = $"[{Environment.MachineName}] {message}".Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
                        var content = new StringContent($"{{\"content\": \"{escaped}\"}}", Encoding.UTF8, "application/json");
                        await _httpClient.PostAsync(webhookUrl, content);
                    }
                }
            }
            catch { }
        }

        private void chkAdminMode_Checked(object sender, RoutedEventArgs e)
        {
            if (ctrlX != null) ctrlX.btnHome.Visibility = Visibility.Visible;
            if (ctrlY != null) ctrlY.btnHome.Visibility = Visibility.Visible;
            if (ctrlZ != null) ctrlZ.btnHome.Visibility = Visibility.Visible;
        }

        private void chkAdminMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (ctrlX != null) ctrlX.btnHome.Visibility = Visibility.Collapsed;
            if (ctrlY != null) ctrlY.btnHome.Visibility = Visibility.Collapsed;
            if (ctrlZ != null) ctrlZ.btnHome.Visibility = Visibility.Collapsed;
        }

        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!(sender is TextBox tb)) return;
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c) && c != '.' && c != '-') { e.Handled = true; return; }
                if (c == '.' && tb.Text.Contains(".")) { e.Handled = true; return; }
                if (c == '-' && (tb.Text.Contains("-") || tb.SelectionStart != 0)) { e.Handled = true; return; }
            }
        }

        private void TextBox_GotFocus(object sender, RoutedEventArgs e) { if (sender is TextBox tb) _lastValidValues[tb] = tb.Text; }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (string.IsNullOrWhiteSpace(tb.Text) || tb.Text == "-" || tb.Text == ".") tb.Text = _lastValidValues.ContainsKey(tb) ? _lastValidValues[tb] : "0";
                else if (double.TryParse(tb.Text, out double val)) tb.Text = val.ToString("0.########");
                else tb.Text = _lastValidValues.ContainsKey(tb) ? _lastValidValues[tb] : "0";
            }
        }

        private void TextBox_KeyDown_Execute(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (sender == ctrlX.inTarget || sender == ctrlX.inVel || sender == ctrlX.inAcc)
                {
                    if (ctrlX.cmbMode.SelectedIndex == 1) ExecuteMoveAbsolute(0, ctrlX.inTarget, ctrlX.inVel, ctrlX.inAcc);
                    else ExecuteMoveRelative(0, 1.0, ctrlX.inTarget, ctrlX.inVel, ctrlX.inAcc);
                }
                else if (sender == ctrlY.inTarget || sender == ctrlY.inVel || sender == ctrlY.inAcc)
                {
                    if (ctrlY.cmbMode.SelectedIndex == 1) ExecuteMoveAbsolute(1, ctrlY.inTarget, ctrlY.inVel, ctrlY.inAcc);
                    else ExecuteMoveRelative(1, 1.0, ctrlY.inTarget, ctrlY.inVel, ctrlY.inAcc);
                }
                else if (sender == ctrlZ.inTarget || sender == ctrlZ.inVel || sender == ctrlZ.inAcc)
                {
                    if (ctrlZ.cmbMode.SelectedIndex == 1) ExecuteMoveAbsolute(2, ctrlZ.inTarget, ctrlZ.inVel, ctrlZ.inAcc);
                    else ExecuteMoveRelative(2, 1.0, ctrlZ.inTarget, ctrlZ.inVel, ctrlZ.inAcc);
                }
            }
        }

        private void cmbMacroAxis_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbMacroAxis == null || inMacroTarget == null || unitMacroTarget == null || inMacroVel == null || unitMacroVel == null || cmbMacroMode == null) return;
            if (cmbMacroAxis.SelectedIndex == 3)
            {
                cmbMacroMode.IsEnabled = false;
                lblMacroTarget.Text = "대기 시간:";
                inMacroTarget.ToolTip = "대기할 시간을 초(s) 단위로 입력하세요.";
                unitMacroTarget.Text = "s";
                inMacroVel.IsEnabled = false;
                unitMacroVel.Text = "-";
            }
            else
            {
                cmbMacroMode.IsEnabled = true;
                lblMacroTarget.Text = "목표값(거리/좌표):";
                inMacroTarget.ToolTip = null;
                unitMacroTarget.Text = _unitString;
                inMacroVel.IsEnabled = true;
                unitMacroVel.Text = _unitString + "/s";
            }
        }

        private void UpdateUnitLabels()
        {
            string d = _unitString, v = _unitString + "/s", a = _unitString + "/s²";

            if (ctrlX != null) { ctrlX.unitTarget.Text = d; ctrlX.unitVel.Text = v; ctrlX.unitAcc.Text = a; }
            if (ctrlY != null) { ctrlY.unitTarget.Text = d; ctrlY.unitVel.Text = v; ctrlY.unitAcc.Text = a; }
            if (ctrlZ != null) { ctrlZ.unitTarget.Text = d; ctrlZ.unitVel.Text = v; ctrlZ.unitAcc.Text = a; }

            if (cmbMacroAxis != null && cmbMacroAxis.SelectedIndex != 3) { if (unitMacroTarget != null) unitMacroTarget.Text = d; if (unitMacroVel != null) unitMacroVel.Text = v; }

            if (unitCircleDiameter != null) unitCircleDiameter.Text = d;
            if (unitCircleZDist != null) unitCircleZDist.Text = d;
            if (unitNudgeDist != null) unitNudgeDist.Text = d;
            if (unitCircleVel != null) unitCircleVel.Text = v;
            if (unitCircleZVel != null) unitCircleZVel.Text = v;
            if (unitNudgeVel != null) unitNudgeVel.Text = v;
        }

        private void cmbUnits_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbUnits == null) return;
            double old = _unitMultiplier;

            if (cmbUnits.SelectedIndex == 0) { _unitMultiplier = 1.0; _unitString = "mm"; }
            else { _unitMultiplier = 1000.0; _unitString = "um"; }

            double factor = _unitMultiplier / old;
            if (factor != 1.0)
            {
                ConvertTextBox(ctrlX?.inTarget, factor); ConvertTextBox(ctrlX?.inVel, factor); ConvertTextBox(ctrlX?.inAcc, factor);
                ConvertTextBox(ctrlY?.inTarget, factor); ConvertTextBox(ctrlY?.inVel, factor); ConvertTextBox(ctrlY?.inAcc, factor);
                ConvertTextBox(ctrlZ?.inTarget, factor); ConvertTextBox(ctrlZ?.inVel, factor); ConvertTextBox(ctrlZ?.inAcc, factor);
                ConvertTextBox(inCircleDiameter, factor); ConvertTextBox(inCircleVel, factor); ConvertTextBox(inCircleZVel, factor);
                ConvertTextBox(inCircleZDist, factor); ConvertTextBox(inNudgeDist, factor); ConvertTextBox(inNudgeVel, factor);

                if (cmbMacroAxis != null && cmbMacroAxis.SelectedIndex != 3) { ConvertTextBox(inMacroTarget, factor); ConvertTextBox(inMacroVel, factor); }

                foreach (var pt in _sequenceManager.ArrayPoints) { pt.AxisPos *= factor; pt.ZPos *= factor; }
                foreach (var cmd in _sequenceManager.MacroSequence) if (cmd.AxisName != "WAIT") { cmd.Target *= factor; cmd.Velocity *= factor; }
                CalculateMacroTimes();
            }

            UpdateUnitLabels();
            UpdateCalcXYVel();
            for (int i = 0; i < 5; i++) UpdateWaypointText(i);
        }

        private void ConvertTextBox(TextBox tb, double factor)
        {
            if (tb != null && double.TryParse(tb.Text, out double val)) tb.Text = (val * factor).ToString("0.########");
        }

        private void btnSearch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _deviceController.IsSimulationMode = chkSimulationMode.IsChecked == true;
                cmbSerialNo.Items.Clear();
                foreach (string serial in _deviceController.GetDeviceList()) cmbSerialNo.Items.Add(serial);

                if (cmbSerialNo.Items.Count > 0)
                {
                    cmbSerialNo.SelectedIndex = 0;
                    txtGlobalStatus.Text = _deviceController.IsSimulationMode ? "가상 기기 검색 완료" : "기기 검색 완료";
                }
                else MessageBox.Show("인식된 기기가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { MessageBox.Show($"검색 오류:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cmbSerialNo.SelectedItem == null) return;
                btnConnect.IsEnabled = false; btnSearch.IsEnabled = false;
                txtGlobalStatus.Text = "연결 중..."; txtGlobalStatus.Foreground = Brushes.Orange;

                await _deviceController.ConnectAsync(cmbSerialNo.SelectedItem.ToString());
                txtGlobalStatus.Text = "연결 성공 및 채널 활성화됨"; txtGlobalStatus.Foreground = Brushes.Blue;
                btnDisconnect.IsEnabled = true; btnSearch.IsEnabled = true;
                _uiTimer.Start();
                LogAction("System", "연결 성공", cmbSerialNo.SelectedItem.ToString(), "-");
            }
            catch (Exception ex)
            {
                EnterFault(nameof(btnConnect_Click), ex);
            }
        }


        private void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _uiTimer.Stop();
                _deviceController.Disconnect();
                txtGlobalStatus.Text = "연결 해제됨"; txtGlobalStatus.Foreground = Brushes.Gray;
                btnConnect.IsEnabled = true; btnDisconnect.IsEnabled = false;

                if (ctrlX != null) ctrlX.txtPos.Text = "현재 위치: -";
                if (ctrlY != null) ctrlY.txtPos.Text = "현재 위치: -";
                if (ctrlZ != null) ctrlZ.txtPos.Text = "현재 위치: -";
                LogAction("System", "연결 해제", "-", "-");
            }
            catch (Exception ex) { MessageBox.Show($"연결 해제 중 오류 발생:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            UpdateAxisUI(0, ctrlX.txtPos, ctrlX.txtStatus, ctrlX.txtTime, ctrlX.btnHome, ctrlX.btnMinus, ctrlX.btnPlus, ctrlX.btnGo, ctrlX.btnStop);
            UpdateAxisUI(1, ctrlY.txtPos, ctrlY.txtStatus, ctrlY.txtTime, ctrlY.btnHome, ctrlY.btnMinus, ctrlY.btnPlus, ctrlY.btnGo, ctrlY.btnStop);
            UpdateAxisUI(2, ctrlZ.txtPos, ctrlZ.txtStatus, ctrlZ.txtTime, ctrlZ.btnHome, ctrlZ.btnMinus, ctrlZ.btnPlus, ctrlZ.btnGo, ctrlZ.btnStop);

            if (_isRecording)
            {
                DateTime now = DateTime.Now;
                for (int i = 0; i < 3; i++)
                {
                    if (!_deviceController.IsConnected(i)) continue;
                    double curPos = (double)_deviceController.GetPosition(i) * _unitMultiplier;

                    if (_isDirectMoving[i] && !_deviceController.IsMoving(i))
                    {
                        double finalDist = _isInterrupted[i] ? (curPos - _lastRecordedPos[i]) : _plannedDist[i];
                        lock (_recordLock)
                        {
                            _recordedPath.Add(new MacroCommand
                            {
                                Index = _recordedPath.Count + 1,
                                AxisName = i == 0 ? "X" : (i == 1 ? "Y" : "Z"),
                                AxisId = i,
                                Mode = "Rel",
                                Target = finalDist,
                                Velocity = _plannedVel[i],
                                IsSync = false
                            });
                        }
                        _lastRecordedPos[i] = curPos; _lastRecordedTime[i] = now; _isDirectMoving[i] = false;
                    }
                    else if (!_isDirectMoving[i] && _deviceController.IsMoving(i))
                    {
                        double deltaPos = curPos - _lastRecordedPos[i];
                        double dt = (now - _lastRecordedTime[i]).TotalSeconds;
                        if (Math.Abs(deltaPos) > 0.0005 && dt > 0)
                        {
                            double v = Math.Min(Math.Abs(deltaPos) / dt, 2.0);
                            lock (_recordLock)
                            {
                                _recordedPath.Add(new MacroCommand
                                {
                                    Index = _recordedPath.Count + 1,
                                    AxisName = i == 0 ? "X" : (i == 1 ? "Y" : "Z"),
                                    AxisId = i,
                                    Mode = "Rel",
                                    Target = deltaPos,
                                    Velocity = Math.Round(v, 4),
                                    IsSync = false
                                });
                            }
                            _lastRecordedPos[i] = curPos; _lastRecordedTime[i] = now;
                        }
                    }
                }
            }

            bool active = _sequenceManager.IsMacroRunning || _sequenceManager.IsArrayRunning || _isRecording ||
                          (_deviceController.IsConnected(0) && _deviceController.IsMoving(0)) ||
                          (_deviceController.IsConnected(1) && _deviceController.IsMoving(1)) ||
                          (_deviceController.IsConnected(2) && _deviceController.IsMoving(2));
            UpdatePollingMode(active);

            if (_sequenceManager.IsMacroRunning &&
                _sequenceManager.CurrentMacroIndex >= 0 &&
                _sequenceManager.CurrentMacroIndex < _sequenceManager.MacroSequence.Count)
            {
                var cmd = _sequenceManager.MacroSequence[_sequenceManager.CurrentMacroIndex];
                if (cmd.AxisId < 3) _timeRemaining[cmd.AxisId] = cmd.RemainingTime;
            }

            if (_sequenceManager.IsMacroRunning && int.TryParse(txtMacroLoops?.Text, out int totalLoops))
            {
                double remain = _sequenceManager.GetRemainingTotalSeconds(totalLoops);
                UpdateTotalRemainingTime(remain);
            }
        }

        private void SetTotalRemainingTimeText(string text)
        {
            if (txtTotalTime != null) txtTotalTime.Text = text;
            if (FindName("txtMainTotalTime") is TextBlock tbMain) tbMain.Text = text;
        }

        private void UpdateTotalRemainingTime(double remainSeconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, remainSeconds));
            SetTotalRemainingTimeText($"총 남은 시간: {ts:mm\\:ss}");
        }

        private void UpdatePollingMode(bool active)
        {
            if (_activePollingMode == active) return;
            _activePollingMode = active;

            _uiTimer.Interval = active ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromMilliseconds(1000);
        }

        private void UpdateAxisUI(int index, TextBlock txtPos, TextBlock txtStatus, TextBlock txtTime, Button btnHome, Button btnMinus, Button btnPlus, Button btnGo, Button btnStop)
        {
            if (!_deviceController.IsConnected(index)) return;

            decimal realPos = _deviceController.GetPosition(index);
            double displayPos = (double)realPos * _unitMultiplier;

            txtPos.Text = $"현재 위치: {displayPos:F6} {_unitString}";

            bool axisMoving = _deviceController.IsMoving(index);
            if (axisMoving)
            {
                _seenMovingAfterCommand[index] = true;
                _moveCommanded[index] = false;
                bool isSettling = false;

                if (_hasTargetTime[index])
                {
                    double remains = (_targetEndTime[index] - DateTime.Now).TotalSeconds;
                    _timeRemaining[index] = remains > 0 ? remains : 0;
                    isSettling = _timeRemaining[index] <= 0;
                }
                else
                {
                    _timeRemaining[index] = 0;
                    isSettling = true;
                }

                txtStatus.Text = isSettling ? "상태: 정착 대기" : "상태: 이동 중";
                txtStatus.Foreground = isSettling ? Brushes.Orange : Brushes.Blue;
                btnHome.IsEnabled = false; btnMinus.IsEnabled = false; btnPlus.IsEnabled = false; btnGo.IsEnabled = false; btnStop.IsEnabled = true;
            }
            else
            {
                bool inGrace = (DateTime.Now - _targetIssuedAt[index]).TotalMilliseconds < 500;
                if (!inGrace && (!_hasTargetTime[index] || _seenMovingAfterCommand[index]))
                    _hasTargetTime[index] = false;


                if (!_moveCommanded[index] && _timeRemaining[index] <= 0.25) _timeRemaining[index] = 0;

                txtStatus.Text = "상태: 멈춤"; txtStatus.Foreground = Brushes.Red;
                btnHome.IsEnabled = (!_sequenceManager.IsMacroRunning && !_sequenceManager.IsArrayRunning) && (chkAdminMode.IsChecked == true);
                btnMinus.IsEnabled = !_sequenceManager.IsMacroRunning && !_sequenceManager.IsArrayRunning;
                btnPlus.IsEnabled = !_sequenceManager.IsMacroRunning && !_sequenceManager.IsArrayRunning;
                btnGo.IsEnabled = !_sequenceManager.IsMacroRunning && !_sequenceManager.IsArrayRunning;
                btnStop.IsEnabled = false;
            }
            txtTime.Text = axisMoving && (_timeRemaining[index] <= 0 || !_hasTargetTime[index])
                ? "남은 시간: 정착 대기"
                : $"남은 시간: {_timeRemaining[index]:F1}초";
        }
        private async Task<bool> WaitUntilStopped(int index)
    => await _deviceController.WaitUntilStoppedAsync(index, () => !_sequenceManager.IsMacroRunning && !_sequenceManager.IsArrayRunning);

        private async Task RunHomeAxis(int index)

        {
            LogAction($"UI CH {index + 1}", "Home 복귀 시도", "-", "-");
            try { await _deviceController.HomeAsync(index); }
            catch (Exception ex) { MessageBox.Show($"원점 복귀 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void StopAxis(int index)
        {
            _deviceController.StopProfiled(index);
            _timeRemaining[index] = 0;
            _hasTargetTime[index] = false;
            if (_isRecording && _isDirectMoving[index]) _isInterrupted[index] = true;
            _moveCommanded[index] = false; _isDirectMoving[index] = false;
            LogAction($"UI CH {index + 1}", "안전 정지", "-", "-");
        }

        private async Task RunMoveRelative(int index, double dirMultiplier, double dist, double vel, double acc, bool isMacro = false)
        {
            if (!_deviceController.IsConnected(index)) return;
            await RunMoveAbsolute(index, (double)(_deviceController.GetPosition(index) + (decimal)(dist / _unitMultiplier) * (decimal)dirMultiplier) * _unitMultiplier, vel, acc, isMacro);
        }

        private async Task RunMoveAbsolute(int index, double pos, double vel, double acc, bool isMacro = false)
        {
            if (!_deviceController.IsConnected(index)) return;
            if (_isRecording && !isMacro)
            {
                _plannedDist[index] = pos - ((double)_deviceController.GetPosition(index) * _unitMultiplier);
                _plannedVel[index] = vel;
                _isInterrupted[index] = false;
                _isDirectMoving[index] = true;
            }
            _targetPos[index] = pos; _targetVel[index] = vel; _moveCommanded[index] = true;

            double calcDuration = vel > 0 ? Math.Abs(pos - ((double)_deviceController.GetPosition(index) * _unitMultiplier)) / vel : 0;
            _targetEndTime[index] = DateTime.Now.AddSeconds(calcDuration);
            _timeRemaining[index] = calcDuration;
            _hasTargetTime[index] = true;
            _targetIssuedAt[index] = DateTime.Now;
            _seenMovingAfterCommand[index] = false;


            LogAction($"UI CH {index + 1}", "이동 명령 하달", $"{pos}", $"{vel}");

            try
            {
                await _deviceController.MoveAbsoluteAsync(index, (decimal)(pos / _unitMultiplier), (decimal)(vel / _unitMultiplier), (decimal)(acc / _unitMultiplier), isMacro);
            }
            catch (InvalidOperationException ex)
            {
                _moveCommanded[index] = false;
                _timeRemaining[index] = 0;
                _hasTargetTime[index] = false;
                _isDirectMoving[index] = false;
                LogAction($"UI CH {index + 1}", "이동 거부", $"{pos}", $"{vel}");
                txtGlobalStatus.Text = $"축 {index + 1} 이동 거부: {ex.Message}";
                txtGlobalStatus.Foreground = Brushes.OrangeRed;
                MessageBox.Show($"축 {index + 1} 이동이 Safety Gate에서 거부되었습니다.\n{ex.Message}", "이동 거부", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                _moveCommanded[index] = false;
                _timeRemaining[index] = 0;
                _hasTargetTime[index] = false;
                _isDirectMoving[index] = false;
                txtGlobalStatus.Text = $"축 {index + 1} 이동 오류";
                txtGlobalStatus.Foreground = Brushes.Red;
                MessageBox.Show($"축 {index + 1} 이동 중 오류 발생:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteMoveRelative(int axis, double dir, TextBox txtTarget, TextBox txtVel, TextBox txtAcc)
        {
            if (double.TryParse(txtTarget.Text, out double dist) && double.TryParse(txtVel.Text, out double vel) && double.TryParse(txtAcc.Text, out double acc))
                _ = RunMoveRelative(axis, dir, dist, vel, acc);
        }

        private void ExecuteMoveAbsolute(int axis, TextBox txtTarget, TextBox txtVel, TextBox txtAcc)
        {
            if (double.TryParse(txtTarget.Text, out double pos) && double.TryParse(txtVel.Text, out double vel) && double.TryParse(txtAcc.Text, out double acc))
                _ = RunMoveAbsolute(axis, pos, vel, acc);
        }

        private DateTime[] _lastJogTime = new DateTime[3];
        private void HandleJog(int index, Slider slider)
        {
            if ((DateTime.Now - _lastJogTime[index]).TotalMilliseconds < 50) return;
            _lastJogTime[index] = DateTime.Now;
            _hasTargetTime[index] = false;
            _deviceController.StartJog(index, slider.Value);
        }

        private void StopJog(int index, Slider slider)
        {
            slider.Value = 0;
            _hasTargetTime[index] = false;
            _deviceController.StopProfiled(index);
            LogAction($"UI CH {index + 1}", "조그 이동 종료", "-", "-");
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (chkEnableNudge?.IsChecked != true || _sequenceManager.IsMacroRunning || _sequenceManager.IsArrayRunning || e.OriginalSource is TextBox) return;
            int axis = -1; double dir = 0;

            switch (e.Key)
            {
                case Key.Right: axis = 0; dir = 1; break;
                case Key.Left: axis = 0; dir = -1; break;
                case Key.Up: axis = 1; dir = 1; break;
                case Key.Down: axis = 1; dir = -1; break;
                case Key.PageUp: axis = 2; dir = 1; break;
                case Key.PageDown: axis = 2; dir = -1; break;
            }

            if (axis != -1)
            {
                e.Handled = true;
                if (!_deviceController.IsConnected(axis) || _deviceController.IsMoving(axis)) return;
                if (double.TryParse(inNudgeDist.Text, out double dist) && double.TryParse(inNudgeVel.Text, out double vel)) _ = RunMoveRelative(axis, dir, dist, vel, 1.0);
            }
        }

        private void btnSaveWp_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !int.TryParse(btn.Tag?.ToString(), out int slot)) return;
            bool savedAny = false;
            for (int i = 0; i < 3; i++)
            {
                if (_deviceController.IsConnected(i)) { _waypoints[slot, i] = _deviceController.GetPosition(i); savedAny = true; }
            }
            if (savedAny) { UpdateWaypointText(slot); LogAction("All", "Waypoint 저장", $"Slot {slot + 1}", "-"); }
            else MessageBox.Show("연결된 기기가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void btnGoWp_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !int.TryParse(btn.Tag?.ToString(), out int slot)) return;
            TextBox[] vels = { ctrlX.inVel, ctrlY.inVel, ctrlZ.inVel };
            TextBox[] accs = { ctrlX.inAcc, ctrlY.inAcc, ctrlZ.inAcc };

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                if (_waypoints[slot, i].HasValue && _deviceController.IsConnected(i))
                {
                    double t = (double)_waypoints[slot, i].Value * _unitMultiplier;
                    double v = double.TryParse(vels[i].Text, out double parseV) ? parseV : 0.5;
                    double a = double.TryParse(accs[i].Text, out double parseA) ? parseA : 0.5;
                    tasks.Add(RunMoveAbsolute(i, t, v, a));
                }
            }
            if (tasks.Count > 0) { LogAction("All", "Waypoint 이동 시작", $"Slot {slot + 1}", "-"); await Task.WhenAll(tasks); }
        }

        private void UpdateWaypointText(int slot)
        {
            string F(int i) => _waypoints[slot, i].HasValue ? $"{((double)_waypoints[slot, i].Value * _unitMultiplier):0.########} {_unitString}" : "-";
            if (FindName($"txtWp{slot}") is TextBlock tb) tb.Text = $"Slot {slot + 1}: X={F(0)}, Y={F(1)}, Z={F(2)}";
        }

        private void btnExportLog_Click(object sender, RoutedEventArgs e) => _fileManager.ExportLog(_actionLog);
        private void btnDownloadCsv_Click(object sender, RoutedEventArgs e) => _fileManager.DownloadCsvTemplate();

        private void CalculateMacroTimes()
        {
            double[] currentPos = new double[3];
            for (int i = 0; i < 3; i++) currentPos[i] = _deviceController.IsConnected(i) ? (double)_deviceController.GetPosition(i) * _unitMultiplier : 0;
            _engine.CalculateMacroEstimatedTimes(_sequenceManager.MacroSequence, currentPos);
            dgMacro.Items.Refresh();
            if (btnMainStartMacro != null) btnMainStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0;

            double seqTotal = 0; foreach (var c in _sequenceManager.MacroSequence) seqTotal += c.BillingTime;
            _sequenceManager.SetTotalEstimatedPerLoop(seqTotal);
            int loops = int.TryParse(txtMacroLoops?.Text, out int l) ? l : 1;
            UpdateTotalRemainingTime(seqTotal * loops);

        }

        private void dgMacro_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(new Action(() => CalculateMacroTimes()), DispatcherPriority.Background);

        private void btnLoadCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog ofd = new OpenFileDialog { Filter = "CSV 파일 (*.csv)|*.csv" };
                if (ofd.ShowDialog() == true)
                {
                    _sequenceManager.MacroSequence.Clear();
                    foreach (var cmd in _engine.ParseMacroCsv(ofd.FileName)) _sequenceManager.MacroSequence.Add(cmd);
                    CalculateMacroTimes();
                    btnStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0;
                    MessageBox.Show($"총 {_sequenceManager.MacroSequence.Count}개의 명령을 불러왔습니다.", "불러오기 완료");
                }
            }
            catch (Exception ex) { MessageBox.Show($"CSV 로드 실패: {ex.Message}"); }
        }

        private void btnAddMacro_Click(object sender, RoutedEventArgs e)
        {
            bool isWait = cmbMacroAxis.SelectedIndex == 3;
            double target = 0;
            double vel = 0;

            bool targetOk = double.TryParse(inMacroTarget.Text, out target);
            bool velOk = isWait || double.TryParse(inMacroVel.Text, out vel);

            if (targetOk && velOk)
            {
                MacroCommand cmd = new MacroCommand
                {
                    Index = _sequenceManager.MacroSequence.Count + 1,
                    AxisName = isWait ? "WAIT" : cmbMacroAxis.Text,
                    AxisId = cmbMacroAxis.SelectedIndex,
                    Mode = isWait ? "None" : cmbMacroMode.Text,
                    Target = target,
                    Velocity = isWait ? 0 : vel,
                    IsSync = chkMacroSync.IsChecked == true
                };
                _sequenceManager.MacroSequence.Add(cmd);
                CalculateMacroTimes();
                btnStartMacro.IsEnabled = true; dgMacro.ScrollIntoView(cmd);
            }
            else
            {
                MessageBox.Show("목표값과 속도에는 숫자만 입력해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnInlineDeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MacroCommand cmd)
            {
                _sequenceManager.MacroSequence.Remove(cmd);
                for (int i = 0; i < _sequenceManager.MacroSequence.Count; i++) _sequenceManager.MacroSequence[i].Index = i + 1;
                CalculateMacroTimes(); dgMacro.Items.Refresh();
                btnStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0;
            }
        }

        private void btnClearMacro_Click(object sender, RoutedEventArgs e) { _sequenceManager.MacroSequence.Clear(); btnStartMacro.IsEnabled = false; SetTotalRemainingTimeText("총 남은 시간: --"); }

        private void HelixInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateCalcXYVel();

        private void UpdateCalcXYVel()
        {
            if (inCircleVel == null || txtCircleTotalVel == null) return;

            double vZ = 0;
            double.TryParse(inCircleZVel?.Text, out vZ);

            if (double.TryParse(inCircleDiameter.Text, out double d) && double.TryParse(inCircleZDist.Text, out double zDistPerTurn) &&
                vZ > 0 && int.TryParse(inCircleSteps.Text, out int steps) && int.TryParse(inCircleLoops.Text, out int loops))
            {
                double vXY = _engine.CalculateHelixVelocity(d, zDistPerTurn, vZ, steps, loops);
                if (vXY > 0)
                {
                    string newVxyStr = vXY.ToString("0.########");
                    if (inCircleVel.Text != newVxyStr)
                    {
                        inCircleVel.Text = newVxyStr;
                    }
                    inCircleVel.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF8, 0xFF));
                }
                else
                {
                    inCircleVel.Background = Brushes.White;
                }
            }
            else
            {
                inCircleVel.Background = Brushes.White;
            }

            double currentVxy = 0;
            double.TryParse(inCircleVel.Text, out currentVxy);
            double totalVel = System.Math.Sqrt(currentVxy * currentVxy + vZ * vZ);
            txtCircleTotalVel.Text = totalVel.ToString("0.000000");
        }

        private async void btnDrawCircle_Click(object sender, RoutedEventArgs e)
        {
            if (!_deviceController.IsConnected(0) || !_deviceController.IsConnected(1)) { MessageBox.Show("X, Y축 모두 연결 필요", "오류", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            if (!double.TryParse(inCircleDiameter.Text, out double d) || !double.TryParse(inCircleZDist.Text, out double zD) ||
                !double.TryParse(inCircleVel.Text, out double vXY) || !double.TryParse(inCircleZVel.Text, out double vZ) ||
                !int.TryParse(inCircleSteps.Text, out int steps) || !int.TryParse(inCircleLoops.Text, out int loops)) return;

            bool zE = _deviceController.IsConnected(2);
            if (zD != 0 && !zE) { MessageBox.Show("Z축 미연결", "오류", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            btnStartMacro.IsEnabled = false; btnDrawCircle.IsEnabled = false; btnStopMacro.IsEnabled = true;
            txtMacroStatus.Text = (zD != 0) ? "나선형 그리기 실행 중..." : "원형 그리기 실행 중..."; txtMacroStatus.Foreground = Brushes.Blue;
            try
            {
                double sX = (double)_deviceController.GetPosition(0);
                double sY = (double)_deviceController.GetPosition(1);
                double sZ = zE ? (double)_deviceController.GetPosition(2) : 0;

                LogAction("XYZ", "원/나선 그리기 시작", $"직경:{d}, 루프:{loops}", $"XY속도:{vXY}");
                await _sequenceManager.RunCirclePatternAsync(sX, sY, sZ, d / _unitMultiplier, zD / _unitMultiplier, vXY / _unitMultiplier, vZ / _unitMultiplier, steps, loops, zE);
                txtMacroStatus.Text = "대기 중"; txtMacroStatus.Foreground = Brushes.Black;
            }
            catch (Exception ex)
            {
                _sequenceManager.StopAll();
                txtMacroStatus.Text = "원/나선 실행 중 오류로 정지됨";
                txtMacroStatus.Foreground = Brushes.Red;
                MessageBox.Show($"원/나선 실행 중 오류가 발생하여 긴급 정지했습니다.\n{ex.Message}", "비상 정지", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0;
                btnDrawCircle.IsEnabled = true;
                btnStopMacro.IsEnabled = false;
            }
        }

        private async void btnMoveAngle_Click(object sender, RoutedEventArgs e)
        {
            if (!_deviceController.IsConnected(0) || !_deviceController.IsConnected(1)) { MessageBox.Show("X,Y축 연결 필요", "오류", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (double.TryParse(inAngle.Text, out double ang) && double.TryParse(inAngleDist.Text, out double dist) && double.TryParse(inAngleVel.Text, out double vel))
            {
                var calc = _engine.CalculateAngleMovement(ang, dist, vel);
                btnMoveAngle.IsEnabled = false; _sequenceManager.IsMacroRunning = true;
                LogAction("XY", "각도 지정 이동 시작", $"각도:{ang}, 거리:{dist}", $"속도:{vel}");

                try
                {
                    Task tx = calc.VelX > 0 ? RunMoveRelative(0, calc.DirX, calc.DistanceX, calc.VelX, Math.Max(calc.VelX * 5.0, 0.1), true) : Task.CompletedTask;
                    Task ty = calc.VelY > 0 ? RunMoveRelative(1, calc.DirY, calc.DistanceY, calc.VelY, Math.Max(calc.VelY * 5.0, 0.1), true) : Task.CompletedTask;
                    await Task.WhenAll(tx, ty);
                    if (calc.VelX > 0) await WaitUntilStopped(0);
                    if (calc.VelY > 0) await WaitUntilStopped(1);
                    LogAction("XY", "각도 이동 완료", "-", "-");
                }
                catch { }
                finally { _sequenceManager.IsMacroRunning = false; btnMoveAngle.IsEnabled = true; }
            }
        }

        private async void btnStartMacro_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                dgMacro.CommitEdit(DataGridEditingUnit.Row, true);
                if (_sequenceManager.MacroSequence.Count == 0) return;

                if (!int.TryParse(txtMacroLoops.Text, out int totalLoops) || totalLoops < 1) { totalLoops = 1; txtMacroLoops.Text = "1"; }

                CalculateMacroTimes();
                btnStartMacro.IsEnabled = false; btnStopMacro.IsEnabled = true;
                btnStartMacro.Content = "매크로 시작"; if (btnMainStartMacro != null) { btnMainStartMacro.Content = "▶ 매크로 실행"; btnMainStartMacro.IsEnabled = false; }
                if (btnMainStopMacro != null) btnMainStopMacro.IsEnabled = true;

                txtMacroStatus.Text = "매크로 실행 중..."; txtMacroStatus.Foreground = Brushes.Blue;
                LogAction("Macro", "매크로 시작", $"총 {_sequenceManager.MacroSequence.Count}단계", "-");

                bool applySlope = chkMacroApplySlope?.IsChecked == true;
                double pureSlope = applySlope ? _engine.CalculatePureSlope(_sequenceManager.ArrayPoints) : 0;
                int slopeAxis = cmbArrayAxis != null ? cmbArrayAxis.SelectedIndex : 0;
                if (applySlope) LogAction("Macro", "기울기 보정 구동", $"축:{(slopeAxis == 0 ? "X" : "Y")}", $"m:{pureSlope:F6}");

                try
                {
                    await _sequenceManager.RunMacroSequenceAsync(totalLoops, chkNotifyEveryLoop.IsChecked == true, applySlope, pureSlope, slopeAxis);
                }
                catch (Exception ex)
                {
                    _sequenceManager.StopAll();
                    MessageBox.Show($"매크로 실행 중 오류가 발생하여 긴급 정지했습니다.\n{ex.Message}", "비상 정지", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                btnStartMacro.IsEnabled = true; btnStopMacro.IsEnabled = false;
                if (btnMainStartMacro != null) btnMainStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0;
                if (btnMainStopMacro != null) btnMainStopMacro.IsEnabled = false;

                txtMacroStatus.Text = "매크로 완료 / 대기 중"; txtMacroStatus.Foreground = Brushes.Black;
                UpdateTotalRemainingTime(0);
                dgMacro.Items.Refresh();
            }
            catch (Exception ex)
            {
                EnterFault(nameof(btnStartMacro_Click), ex);
            }
        }


        private void btnStopMacro_Click(object sender, RoutedEventArgs e)
        {
            _sequenceManager.StopAll();
            txtMacroStatus.Text = "강제 정지됨"; txtMacroStatus.Foreground = Brushes.Red;
            btnStartMacro.Content = "매크로 시작"; btnStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0; btnStopMacro.IsEnabled = false; btnDrawCircle.IsEnabled = true;
            if (btnMainStartMacro != null) { btnMainStartMacro.Content = "▶ 매크로 실행"; btnMainStartMacro.IsEnabled = _sequenceManager.MacroSequence.Count > 0; }
            if (btnMainStopMacro != null) btnMainStopMacro.IsEnabled = false;
            UpdateTotalRemainingTime(0);
        }

        private void btnPauseMacro_Click(object sender, RoutedEventArgs e)
        {
            btnStopMacro_Click(sender, e);
        }

        private void LoadPresets() { cmbArrayPresets.Items.Clear(); foreach (var name in _presetManager.GetPresetNames()) cmbArrayPresets.Items.Add(name); }

        private void btnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            string name = txtPresetName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { MessageBox.Show("이름 입력", "알림", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            List<string> ptStrs = new List<string>();
            foreach (var pt in _sequenceManager.ArrayPoints) ptStrs.Add($"{pt.AxisPos},{pt.ZPos}");

            string newLine = $"{name}|{cmbArrayAxis.SelectedIndex}|{cmbArrayGapDir.SelectedIndex}|{string.Join(";", ptStrs)}|{txtArrayPrintDist.Text}|{txtArrayPrintVel.Text}|{txtArrayDownVel.Text}|{txtArrayGapVel.Text}|{txtArrayGapDist.Text}|{txtArrayLoops.Text}";
            _presetManager.SavePreset(name, newLine);
            LoadPresets(); cmbArrayPresets.SelectedItem = name;
        }

        private void btnDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (cmbArrayPresets.SelectedItem == null) return;
            _presetManager.DeletePreset(cmbArrayPresets.SelectedItem.ToString());
            LoadPresets(); txtPresetName.Clear();
        }

        private void cmbArrayPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbArrayPresets.SelectedItem == null) return;
            string[] parts = _presetManager.LoadPresetData(cmbArrayPresets.SelectedItem.ToString());
            if (parts != null && parts.Length >= 10)
            {
                txtPresetName.Text = parts[0];
                cmbArrayAxis.SelectedIndex = int.TryParse(parts[1], out int ax) ? ax : 0;
                cmbArrayGapDir.SelectedIndex = int.TryParse(parts[2], out int di) ? di : 0;
                _sequenceManager.ArrayPoints.Clear();
                foreach (string pStr in parts[3].Split(';'))
                {
                    string[] coords = pStr.Split(',');
                    if (coords.Length == 2 && double.TryParse(coords[0], out double x) && double.TryParse(coords[1], out double z)) _sequenceManager.ArrayPoints.Add(new ArrayPoint { AxisPos = x, ZPos = z });
                }
                while (_sequenceManager.ArrayPoints.Count < 2) _sequenceManager.ArrayPoints.Add(new ArrayPoint { AxisPos = 0, ZPos = 0 });
                dgArrayPoints.Items.Refresh();
                txtArrayPrintDist.Text = parts[4]; txtArrayPrintVel.Text = parts[5]; txtArrayDownVel.Text = parts[6]; txtArrayGapVel.Text = parts[7]; txtArrayGapDist.Text = parts[8]; txtArrayLoops.Text = parts[9];
            }
        }

        private void btnSetArrayPoint_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ArrayPoint pt)
            {
                int axis = cmbArrayAxis.SelectedIndex;
                if (_deviceController.IsConnected(axis) && _deviceController.IsConnected(2))
                {
                    pt.AxisPos = (double)_deviceController.GetPosition(axis) * _unitMultiplier;
                    pt.ZPos = (double)_deviceController.GetPosition(2) * _unitMultiplier;
                    dgArrayPoints.Items.Refresh();
                }
                else MessageBox.Show("축 연결 안됨", "연결 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnDeleteArrayPoint_Click(object sender, RoutedEventArgs e)
        {
            if (_sequenceManager.ArrayPoints.Count <= 2) return;
            if (sender is Button btn && btn.DataContext is ArrayPoint pt) { _sequenceManager.ArrayPoints.Remove(pt); dgArrayPoints.Items.Refresh(); }
        }

        private void btnAddArrayPoint_Click(object sender, RoutedEventArgs e)
        {
            if (_sequenceManager.ArrayPoints.Count >= 10) return;
            _sequenceManager.ArrayPoints.Add(new ArrayPoint { AxisPos = 0, ZPos = 0 }); dgArrayPoints.Items.Refresh();
        }

        private (double pDist, double pVel, double dVel, double gVel, double gDist, int loops)? GetArrayParams()
        {
            if (!double.TryParse(txtArrayPrintDist.Text, out double pD) || !double.TryParse(txtArrayPrintVel.Text, out double pV) ||
                !double.TryParse(txtArrayDownVel.Text, out double dV) || !double.TryParse(txtArrayGapVel.Text, out double gV) ||
                !double.TryParse(txtArrayGapDist.Text, out double gD) || !int.TryParse(txtArrayLoops.Text, out int lps) || lps < 1)
                return null;
            return (pD, pV, dV, gV, gD, lps);
        }

        private async void btnStartArray_Click(object sender, RoutedEventArgs e)
        {
            if (!_deviceController.IsConnected(2) || !_deviceController.IsConnected(cmbArrayAxis.SelectedIndex)) { MessageBox.Show("축 미연결", "오류", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var p = GetArrayParams(); if (p == null) return;

            double slopePerStep = _engine.CalculateArraySlope(_sequenceManager.ArrayPoints, p.Value.gDist, cmbArrayGapDir.SelectedIndex);
            double gapDir = cmbArrayGapDir.SelectedIndex == 0 ? 1.0 : -1.0;

            btnStartArray.IsEnabled = false; btnExportArray.IsEnabled = false; btnStopArray.IsEnabled = true;
            txtArrayStatus.Text = "어레이 프린팅 실행 중..."; txtArrayStatus.Foreground = Brushes.Blue;

            try
            {
                await _sequenceManager.RunArrayPrintingAsync(p.Value.loops, p.Value.pDist / _unitMultiplier, p.Value.pVel / _unitMultiplier, p.Value.gDist / _unitMultiplier, p.Value.gVel / _unitMultiplier, p.Value.dVel / _unitMultiplier, slopePerStep / _unitMultiplier, cmbArrayAxis.SelectedIndex, gapDir, chkNotifyEveryLoop.IsChecked == true);
            }
            catch (Exception ex)
            {
                btnStopArray_Click(null, null);
                MessageBox.Show($"어레이 구동 중 오류가 발생했습니다.\n{ex.Message}", "비상 정지", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            btnStartArray.IsEnabled = true; btnExportArray.IsEnabled = true; btnStopArray.IsEnabled = false;
            txtArrayStatus.Text = "완료 (또는 정지됨)"; txtArrayStatus.Foreground = Brushes.Black;
        }

        private void btnExportArray_Click(object sender, RoutedEventArgs e)
        {
            var p = GetArrayParams(); if (p == null) return;
            double slopePerStep = _engine.CalculateArraySlope(_sequenceManager.ArrayPoints, p.Value.gDist, cmbArrayGapDir.SelectedIndex);
            _fileManager.ExportArrayMacroCsv(p.Value.loops, p.Value.pDist, p.Value.pVel, p.Value.gDist, p.Value.gVel, p.Value.dVel, slopePerStep, cmbArrayAxis.SelectedIndex == 0 ? "X" : "Y", cmbArrayGapDir.SelectedIndex == 0 ? 1.0 : -1.0);
        }

        private void btnStopArray_Click(object sender, RoutedEventArgs e)
        {
            _sequenceManager.IsArrayRunning = false;
            for (int i = 0; i < 3; i++) StopAxis(i);
            txtArrayStatus.Text = "비상 정지됨!"; txtArrayStatus.Foreground = Brushes.Red;
            btnStartArray.IsEnabled = true; btnExportArray.IsEnabled = true; btnStopArray.IsEnabled = false;
        }

        private void btnSubmitFeedback_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text) || string.IsNullOrWhiteSpace(txtFeedback.Text)) return;
            try { _fileManager.SaveFeedback(_appDataPath, txtName.Text, txtFeedback.Text); txtFeedback.Clear(); txtName.Clear(); MessageBox.Show("저장 완료"); } catch { }
        }

        private void btnToggleRecord_Click(object sender, RoutedEventArgs e)
        {
            _isRecording = !_isRecording;
            if (_isRecording)
            {
                _recordedPath.Clear();
                DateTime now = DateTime.Now;
                for (int i = 0; i < 3; i++)
                {
                    _lastRecordedPos[i] = _deviceController.IsConnected(i) ? (double)_deviceController.GetPosition(i) * _unitMultiplier : 0;
                    _lastRecordedTime[i] = now;
                }
                btnToggleRecord.Content = "■ 경로 기록 중지";
                btnToggleRecord.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xE0, 0xB2));
                btnSaveRecordedPath.IsEnabled = false;
            }

            else
            {
                btnToggleRecord.Content = "● 경로 기록 시작";
                btnToggleRecord.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xE0, 0xB2));
                btnSaveRecordedPath.IsEnabled = _recordedPath.Count > 0;
                MessageBox.Show($"경로 레코딩이 중지되었습니다. 총 {_recordedPath.Count}개의 포인트가 수집되었습니다.\n[기록 CSV 저장] 버튼을 눌러 매크로 파일로 추출하세요.", "레코딩 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void btnSaveRecordedPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = $"RecordedJogPath_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
                if (sfd.ShowDialog() == true)
                {
                    List<MacroCommand> snapshot;
                    lock (_recordLock) snapshot = new List<MacroCommand>(_recordedPath);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("순서,축(X/Y/Z/WAIT),모드(Abs/Rel),좌표/거리/시간,이동 속도,동시실행(O/X)");
                    foreach (var cmd in snapshot)
                    {

                        sb.AppendLine($"{cmd.Index},{cmd.AxisName},{cmd.Mode},{cmd.Target:0.########},{cmd.Velocity:0.########},{(cmd.IsSync ? "O" : "X")}");
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show("기록된 경로가 성공적으로 저장되었습니다.\n'매크로' 탭의 [CSV 불러오기]를 통해 그대로 구동시킬 수 있습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"저장 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                SaveSettings(); _uiTimer?.Stop();
                if (_sequenceManager != null) { _sequenceManager.NotificationRequested -= SendNotification; _sequenceManager.StopAll(); }
                _deviceController?.Disconnect();
            }
            catch { }
            finally { base.OnClosed(e); }
        }
    }
}
