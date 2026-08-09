using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Thorlabs.MotionControl.DeviceManagerCLI;
using Thorlabs.MotionControl.Benchtop.StepperMotorCLI;
using Thorlabs.MotionControl.GenericMotorCLI;
using Thorlabs.MotionControl.GenericMotorCLI.AdvancedMotor;

namespace nanomaxtest.Controllers
{
    public class DeviceController
    {
        public bool IsSimulationMode { get; set; } = false;

        private bool[] _simIsConnected = new bool[3];
        private decimal[] _simPositions = new decimal[3];
        private bool[] _simIsMoving = new bool[3];
        private readonly object _simStateLock = new object();
        private readonly CancellationTokenSource[] _simMoveCts = new CancellationTokenSource[3];
        private readonly SemaphoreSlim[] _simMoveGate = new[]
        {
            new SemaphoreSlim(1, 1),
            new SemaphoreSlim(1, 1),
            new SemaphoreSlim(1, 1)
        };

        private BenchtopStepperMotor _device;
        private StepperMotorChannel[] _channels = new StepperMotorChannel[3];

        // 축별 이동 명령을 직렬화하여 동일 채널에 Move 명령이 중첩되는 것을 방지합니다.
        private readonly SemaphoreSlim[] _hardwareMoveGate = new[]
        {
            new SemaphoreSlim(1, 1),
            new SemaphoreSlim(1, 1),
            new SemaphoreSlim(1, 1)
        };

        private readonly object _safetyLock = new object();
        private readonly decimal[] _lastAcc = new decimal[3];

        private readonly DateTime[] _lastCmdAt = new DateTime[3];
        private readonly decimal[] _axisMaxVel = new decimal[] { 2.0m, 2.0m, 1.5m };
        private readonly decimal[] _axisMaxAcc = new decimal[] { 5.0m, 5.0m, 2.5m };
        public Action<string> SafetyLog;

        private static decimal Quantize(decimal v, decimal res = 0.00001m)
            => res <= 0 ? v : decimal.Round(v / res, 0, MidpointRounding.AwayFromZero) * res;

        private bool TryApplySafetyGate(int axis, decimal targetPos, decimal targetVel, decimal targetAcc,
            out decimal gatedPos, out decimal gatedVel, out decimal gatedAcc, out string reason)
        {
            gatedPos = Quantize(targetPos);
            gatedVel = Quantize(targetVel);
            gatedAcc = Quantize(targetAcc);
            reason = "";

            if (gatedPos < 0m || gatedPos > 8.0m)
            {
                reason = $"축 {axis} 목표좌표 {gatedPos:F5}mm가 리밋(0~8mm)을 벗어남";
                return false;
            }

            if (gatedVel <= 0m || gatedAcc <= 0m)
            {
                reason = $"축 {axis} 속도/가속도는 양수여야 함 (v={gatedVel:F5}, a={gatedAcc:F5})";
                return false;
            }

            if (gatedVel > _axisMaxVel[axis])
            {
                reason += $" v:{gatedVel:F5}->{_axisMaxVel[axis]:F5} 클램프;";
                gatedVel = _axisMaxVel[axis];
            }
            if (gatedAcc > _axisMaxAcc[axis])
            {
                reason += $" a:{gatedAcc:F5}->{_axisMaxAcc[axis]:F5} 클램프;";
                gatedAcc = _axisMaxAcc[axis];
            }

            lock (_safetyLock)
            {
                if (_lastCmdAt[axis] != default)
                {
                    decimal dt = (decimal)Math.Max((DateTime.UtcNow - _lastCmdAt[axis]).TotalSeconds, 0.01);
                    decimal jerk = Math.Abs(gatedAcc - _lastAcc[axis]) / dt;
                    if (jerk > 50m)
                    {
                        decimal limitedAcc = _lastAcc[axis] + Math.Sign(gatedAcc - _lastAcc[axis]) * 50m * dt;
                        reason += $" jerk 제한으로 a:{gatedAcc:F5}->{limitedAcc:F5};";
                        gatedAcc = Quantize(Math.Max(0.01m, limitedAcc));
                    }
                }
                _lastAcc[axis] = gatedAcc;
                _lastCmdAt[axis] = DateTime.UtcNow;
            }

            return true;
        }

        public IList<string> GetDeviceList()
        {

            if (IsSimulationMode) return new List<string> { "99999999 (가상 장비 시뮬레이터)" };
            DeviceManagerCLI.BuildDeviceList();
            return DeviceManagerCLI.GetDeviceList();
        }

        // [모듈: 강건한 하드웨어 연결 및 비관리형 포인터 충돌 격리]
        // 벤치탑 채널에서 치명적 NRE를 유발하는 LoadMotorConfiguration을 제거하고, 내장 설정을 안전하게 강제 동기화하여 0mm 버그를 해결합니다.
        public async Task ConnectAsync(string serialNo)
        {
            if (IsSimulationMode)
            {
                for (int i = 0; i < 3; i++)
                {
                    _simIsConnected[i] = true;
                    _simPositions[i] = 0m;
                    _simIsMoving[i] = false;
                }
                return;
            }

            string cleanSerial = serialNo?.Trim();
            Thorlabs.MotionControl.DeviceManagerCLI.DeviceManagerCLI.BuildDeviceList();
            await Task.Delay(500); // 비동기 초기화 타이밍 병목 해결을 위한 필수 지연

            await Task.Run(() =>
            {
                try
                {
                    Disconnect();
                    System.Threading.Thread.Sleep(200); // 포트 반환 안정화 딜레이

                    if (string.IsNullOrEmpty(cleanSerial) || cleanSerial.Length < 2)
                        throw new Exception("시리얼 번호가 유효하지 않습니다.");

                    string prefix = cleanSerial.Substring(0, 2);

                    if (prefix == "70")
                    {
                        _device = BenchtopStepperMotor.CreateBenchtopStepperMotor(cleanSerial);
                    }
                    else if (prefix == "71")
                    {
                        throw new Exception($"BPC 피에조 컨트롤러(접두사 71)가 감지되었습니다. 현재 팩토리는 스테퍼(BSC) 전용이므로 BenchtopPiezo 클래스 연동이 필요합니다.");
                    }
                    else
                    {
                        throw new Exception($"시리얼 번호 접두사({prefix})는 현재 BenchtopStepperMotor 클래스와 호환되지 않습니다. 호환되는 Kinesis 드라이버 DLL을 확인하세요.");
                    }

                    if (_device == null)
                    {
                        throw new Exception($"장비({cleanSerial}) 인스턴스를 생성할 수 없습니다. Kinesis DLL 바인딩(x64/x86) 또는 장비 전원 상태를 확인하세요.");
                    }

                    _device.Connect(cleanSerial);
                    System.Threading.Thread.Sleep(500);

                    short[] targetChs = { 3, 2, 1 }; // 논리 인덱스 0(X)->CH3, 1(Y)->CH2, 2(Z)->CH1
                    for (int i = 0; i < 3; i++)
                    {
                        StepperMotorChannel channel = null;
                        // [모듈: 하드웨어 채널 매핑 보정] 물리 하드웨어 채널 순서를 소프트웨어의 직교 좌표계 인덱스에 강제 동기화합니다.
                        short[] candidateIds = { targetChs[i], (short)(targetChs[i] - 1) };

                        foreach (short id in candidateIds)
                        {
                            try
                            {
                                channel = _device.GetChannel(id);
                                if (channel != null) break;
                            }
                            catch (NullReferenceException)
                            {
                                channel = null;
                            }
                            catch
                            {
                                channel = null;
                            }
                        }

                        if (channel == null)
                        {
                            throw new Exception($"모터 채널(축 {i + 1}) 인스턴스를 확보하지 못했습니다. 물리 슬롯 구성을 확인하세요.");
                        }

                        _channels[i] = channel;

                        // [모듈: 모터 구성 파라미터 로드 및 초기화]
                        // SDK 내부 설정 안착을 위해 필수적인 LoadMotorConfiguration을 채널 ID 기반으로 호출합니다.
                        _channels[i].LoadMotorConfiguration(_channels[i].DeviceID);

                        if (!_channels[i].IsSettingsInitialized())
                        {
                            _channels[i].WaitForSettingsInitialized(5000);
                            if (!_channels[i].IsSettingsInitialized())
                            {
                                throw new Exception($"모터 채널(축 {i + 1})의 파라미터 초기화가 지연되고 있습니다.");
                            }
                        }

                        System.Threading.Thread.Sleep(100);

                        var deviceSettings = _channels[i].MotorDeviceSettings;
                        if (deviceSettings == null)
                        {
                            throw new Exception($"모터 채널(축 {i + 1})의 세부 설정(DeviceSettings) 인스턴스를 확보하지 못했습니다.");
                        }

                        _channels[i].SetSettings(deviceSettings, true, true);

                        // [모듈 수정: 메니스커스 파괴 및 충돌 방지] Thorlabs 컨트롤러의 기본 기능인 '백래쉬 보정(Backlash Compensation)' 비활성화. Z축 하강 시 의도적인 오버슛(Overshoot)으로 인해 기판에 충돌하는 현상을 원천 차단.
                        _channels[i].SetBacklash(0m);

                        _channels[i].StartPolling(250);
                        _channels[i].EnableDevice();
                    }
                }
                catch
                {
                    // [모듈 수정: 예외 시 핸들 해제] 초기화 실패 시 기존에 연결된 모터 핸들과 폴링 스레드를 강제 정리하여 교착 상태(Deadlock) 차단
                    Disconnect();
                    throw;
                }
            });
        }

        public void Disconnect()
        {
            if (IsSimulationMode)
            {
                for (int i = 0; i < 3; i++)
                {
                    _simIsConnected[i] = false;
                    _simIsMoving[i] = false;
                    lock (_simStateLock)
                    {
                        _simMoveCts[i]?.Cancel();
                        _simMoveCts[i]?.Dispose();
                        _simMoveCts[i] = null;
                    }
                }
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                if (_channels[i] != null && _channels[i].IsConnected)
                {
                    try { _channels[i].DisableDevice(); } catch { }
                    try { _channels[i].StopPolling(); } catch { }
                    _channels[i] = null;
                }
            }

            if (_device != null)

            {
                _device.Disconnect(true);
                _device = null;
            }
        }


        public bool IsConnected(int index) => IsSimulationMode ? _simIsConnected[index] : (_channels[index] != null && _channels[index].IsConnected);
        public bool IsMoving(int index) => IsSimulationMode ? _simIsMoving[index] : (IsConnected(index) && _channels[index].Status.IsMoving);

        // [모듈: 안전한 좌표 판독 방어막] 장비 물리 파라미터가 초기화되지 않았을 때 UI 타이머가 접근하여 강제 종료되는 현상 원천 차단
        public decimal GetPosition(int index)
        {
            if (IsSimulationMode) return _simPositions[index];
            try
            {
                // 채널이 연결되어 있고, 기계적 설정(DeviceSettings)이 메모리에 안착한 상태에서만 좌표 판독
                if (IsConnected(index) && _channels[index].IsSettingsInitialized())
                {
                    return _channels[index].Position;
                }
                return 0m;
            }
            catch
            {
                return 0m; // DeviceSettingsException 등 예외 발생 시 안전하게 0 반환
            }
        }

        public async Task<bool> WaitUntilStoppedAsync(
            int index,
            Func<bool> checkCancel = null,
            int timeoutMilliseconds = 30000)
        {
            if (!IsConnected(index)) return true;

            if (IsSimulationMode)
            {
                DateTime simDeadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

                while (_simIsMoving[index])
                {
                    if (checkCancel != null && checkCancel()) return false;
                    if (DateTime.UtcNow >= simDeadline)
                        throw new TimeoutException($"축 {index} 정지 확인 시간이 초과되었습니다.");

                    await Task.Delay(50).ConfigureAwait(false);
                }

                return true;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            int stopConfirmCount = 0;

            while (true)
            {
                if (checkCancel != null && checkCancel()) return false;
                if (!IsConnected(index)) return true;

                StepperMotorChannel channel = _channels[index];
                if (channel == null) return true;

                if (!channel.Status.IsMoving)
                {
                    stopConfirmCount++;
                    if (stopConfirmCount >= 3) return true;
                }
                else
                {
                    stopConfirmCount = 0;
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"축 {index} 정지 확인 시간이 초과되었습니다.");

                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        public async Task HomeAsync(int index)

        {
            if (!IsConnected(index)) return;

            if (IsSimulationMode)
            {
                _simIsMoving[index] = true;
                await Task.Delay(1000);
                _simPositions[index] = 0m;
                _simIsMoving[index] = false;
                return;
            }

            await Task.Run(() => _channels[index].Home(60000000));
        }

        // 정지 완료 콜백과 하드웨어 상태 해제를 모두 확인한 후 반환합니다.
        public async Task StopProfiledAsync(int index)
        {
            if (!IsConnected(index)) return;

            if (IsSimulationMode)
            {
                _simIsMoving[index] = false;
                lock (_simStateLock) _simMoveCts[index]?.Cancel();
                await WaitUntilStoppedAsync(index, null, 30000).ConfigureAwait(false);
                return;
            }

            StepperMotorChannel channel = _channels[index];
            if (channel == null) return;

            var stopCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            channel.Stop(_ => stopCompleted.TrySetResult(true));

            Task timeoutTask = Task.Delay(30000);
            Task completedTask = await Task.WhenAny(
                    stopCompleted.Task,
                    timeoutTask)
                .ConfigureAwait(false);

            if (completedTask == timeoutTask)
                throw new TimeoutException($"축 {index} 프로파일 정지 시간이 초과되었습니다.");

            await stopCompleted.Task.ConfigureAwait(false);
            await WaitUntilStoppedAsync(index, null, 30000).ConfigureAwait(false);
        }

        // 창 종료 등 완료 대기가 불가능한 경로를 위한 정지 요청 API입니다.
        public void StopProfiled(int index)
        {
            if (!IsConnected(index)) return;

            if (IsSimulationMode)
            {
                _simIsMoving[index] = false;
                lock (_simStateLock) _simMoveCts[index]?.Cancel();
            }
            else
            {
                _channels[index].Stop(_ => { });
            }
        }

        public void StopImmediate(int index)
        {
            if (IsConnected(index))
            {
                if (IsSimulationMode) _simIsMoving[index] = false;
                else _channels[index].StopImmediate();
            }
        }

        // 비상 정지 명령 후 실제 Moving 상태가 해제될 때까지 기다립니다.
        public async Task StopImmediateAsync(int index)
        {
            if (!IsConnected(index)) return;

            if (IsSimulationMode)
            {
                _simIsMoving[index] = false;
                lock (_simStateLock) _simMoveCts[index]?.Cancel();
            }
            else
            {
                _channels[index].StopImmediate();
            }

            await WaitUntilStoppedAsync(index, null, 10000).ConfigureAwait(false);
        }

        public async Task MoveAbsoluteAsync(int index, decimal targetPos, decimal targetVel, decimal targetAcc, bool ignoreIsMoving = false)
        {
            if (!IsConnected(index)) return;

            // ignoreIsMoving은 호출부 호환성을 위해 유지하며 실제 이동 상태 우회에는 사용하지 않습니다.
            if (!TryApplySafetyGate(index, targetPos, targetVel, targetAcc, out var gatedPos, out var gatedVel, out var gatedAcc, out var reason))
            {
                SafetyLog?.Invoke($"[거부] {reason}");

                throw new InvalidOperationException(reason);
            }
            if (!string.IsNullOrWhiteSpace(reason)) SafetyLog?.Invoke($"[보정] 축{index} {reason}");

            if (IsSimulationMode)
            {
                CancellationTokenSource localCts;
                lock (_simStateLock)
                {
                    _simMoveCts[index]?.Cancel();
                    _simMoveCts[index]?.Dispose();
                    localCts = new CancellationTokenSource();
                    _simMoveCts[index] = localCts;
                }

                await _simMoveGate[index].WaitAsync().ConfigureAwait(false);
                try
                {
                    bool superseded;
                    lock (_simStateLock) superseded = !ReferenceEquals(_simMoveCts[index], localCts);
                    if (superseded) return;

                    _simIsMoving[index] = true;
                    decimal startPos = _simPositions[index];
                    decimal dist = Math.Abs(gatedPos - startPos);
                    int delayMs = gatedVel > 0 ? (int)((dist / gatedVel) * 1000m) : 0;
                    if (delayMs <= 0)
                    {
                        _simPositions[index] = gatedPos;
                        return;
                    }

                    int steps = Math.Max(1, (int)Math.Ceiling(delayMs / 50.0));
                    int stepDelay = Math.Max(1, delayMs / steps);
                    for (int i = 1; i <= steps; i++)
                    {
                        bool keepRunning;
                        lock (_simStateLock) keepRunning = ReferenceEquals(_simMoveCts[index], localCts);
                        if (!keepRunning || !_simIsMoving[index]) break;

                        await Task.Delay(stepDelay, localCts.Token).ConfigureAwait(false);
                        _simPositions[index] = startPos + (gatedPos - startPos) * ((decimal)i / steps);
                    }

                    bool completeNormally;
                    lock (_simStateLock) completeNormally = ReferenceEquals(_simMoveCts[index], localCts);
                    if (completeNormally && _simIsMoving[index]) _simPositions[index] = gatedPos;
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_simStateLock)
                    {
                        if (ReferenceEquals(_simMoveCts[index], localCts))
                        {
                            _simIsMoving[index] = false;
                            _simMoveCts[index]?.Dispose();
                            _simMoveCts[index] = null;
                        }
                    }
                    _simMoveGate[index].Release();
                }
                return;
            }

            // 이전 MoveAbsolute 호출이 반환된 뒤에만 같은 축의 다음 명령을 실행합니다.
            await _hardwareMoveGate[index].WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected(index)) return;

                if (IsMoving(index))
                {
                    throw new InvalidOperationException(
                        $"축 {index}이 아직 이동 또는 정지 처리 중입니다.");
                }

                StepperMotorChannel channel = _channels[index];
                if (channel == null) return;

                // [모듈: 모터 구동 블로킹 예외 캡슐화]
                // 60000000ms 블로킹 대기 중 Stop 명령에 의해 발생하는 하드웨어 강제 중단 예외를 흡수하여 프로그램 크래시 방지
                await Task.Run(() =>
                {
                    channel.SetVelocityParams(gatedVel, gatedAcc);
                    channel.SetMoveAbsolutePosition(gatedPos);
                    try
                    {
                        channel.MoveAbsolute(60000000);
                    }
                    catch (Exception)
                    {
                        // StopProfiledAsync 등 외부 개입으로 인한 이동 취소 시 예외 삼킴
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                _hardwareMoveGate[index].Release();
            }
        }

        public void StartJog(int index, double velocityPercentage)

        {
            if (!IsConnected(index) || velocityPercentage == 0)
            {
                // [모듈 수정: 기구부 기어 충격 방지] 조그 정지 시 감속 프로파일을 무시하는 StopImmediate 대신, 부드럽게 감속하여 기계적 충격을 없애는 StopProfiled(Action 전달) 적용
                StopProfiled(index);
                return;
            }

            if (IsSimulationMode)
            {
                _simIsMoving[index] = true;
                return;
            }

            // 이미 이동 중인 채널에는 MoveContinuous를 중복 전송하지 않습니다.
            if (IsMoving(index)) return;
            if (!_hardwareMoveGate[index].Wait(0)) return;

            try
            {
                decimal maxVel = 2.0m;
                decimal jogVel = (decimal)(Math.Abs(velocityPercentage) / 100.0) * maxVel;
                if (jogVel < 0.1m) jogVel = 0.1m;

                StepperMotorChannel channel = _channels[index];
                if (channel == null) return;

                channel.SetVelocityParams(jogVel, 1.0m);
                channel.MoveContinuous(
                    velocityPercentage > 0
                        ? MotorDirection.Forward
                        : MotorDirection.Backward);
            }
            finally
            {
                _hardwareMoveGate[index].Release();
            }
        }
    }
}
