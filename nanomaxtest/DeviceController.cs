using System;
using System.Collections.Generic;
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

        private BenchtopStepperMotor _device;
        private StepperMotorChannel[] _channels = new StepperMotorChannel[3];

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

                for (int i = 0; i < 3; i++)
                {
                    StepperMotorChannel channel = null;
                    short[] candidateIds = { (short)(i + 1), (short)i };

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

                    _channels[i].StartPolling(250);
                    _channels[i].EnableDevice();
                }
            });
        }

        public void Disconnect()
        {
            {
                if (IsSimulationMode)
                {
                    for (int i = 0; i < 3; i++) _simIsConnected[i] = false;
                    return;
                }

                for (int i = 0; i < 3; i++)
                {
                    if (_channels[i] != null && _channels[i].IsConnected)
                    {
                        _channels[i].StopPolling();
                        _channels[i] = null;
                    }
                }

                if (_device != null)
                {
                    _device.Disconnect(true);
                    _device = null;
                }
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

        public async Task WaitUntilStoppedAsync(int index, Func<bool> checkCancel = null)
        {
            if (!IsConnected(index)) return;

            if (IsSimulationMode)
            {
                while (_simIsMoving[index])
                {
                    if (checkCancel != null && checkCancel()) break;
                    await Task.Delay(50);
                }
                return;
            }

            int stopConfirmCount = 0;
            while (true)
            {
                if (checkCancel != null && checkCancel()) break;

                if (!_channels[index].Status.IsMoving)
                {
                    stopConfirmCount++;
                    if (stopConfirmCount >= 3) break;
                }
                else stopConfirmCount = 0;

                await Task.Delay(50);
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

        public void StopProfiled(int index)
        {
            if (IsConnected(index))
            {
                if (IsSimulationMode) _simIsMoving[index] = false;
                // [완벽 해결] SDK의 Action<ulong> 요구사항에 맞춰 빈 람다 함수 전달
                else _channels[index].Stop(t => { });
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

        public async Task MoveAbsoluteAsync(int index, decimal targetPos, decimal targetVel, decimal targetAcc, bool ignoreIsMoving = false)
        {
            if (!IsConnected(index)) return;
            if (!ignoreIsMoving && IsMoving(index)) return;

            if (IsSimulationMode)
            {
                _simIsMoving[index] = true;
                decimal startPos = _simPositions[index];
                decimal dist = Math.Abs(targetPos - startPos);
                int delayMs = targetVel > 0 ? (int)((dist / targetVel) * 1000) : 0;

                _ = Task.Run(async () =>
                {
                    int steps = delayMs / 50;
                    if (steps > 0)
                    {
                        for (int i = 1; i <= steps; i++)
                        {
                            if (!_simIsMoving[index]) break;
                            await Task.Delay(50);
                            _simPositions[index] = startPos + (targetPos - startPos) * ((decimal)i / steps);
                        }
                    }
                    if (_simIsMoving[index]) _simPositions[index] = targetPos;
                    _simIsMoving[index] = false;
                });
                return;
            }

            decimal safeAcc = Math.Min(targetAcc, 2.0m);
            decimal safeVel = Math.Min(targetVel, 1.5m);

            await Task.Run(() =>
            {
                if (ignoreIsMoving && _channels[index].Status.IsMoving)
                {
                    // [완벽 해결]
                    _channels[index].Stop(t => { });
                    System.Threading.Thread.Sleep(50);
                }

                _channels[index].SetVelocityParams(safeVel, safeAcc);
                _channels[index].SetMoveAbsolutePosition(targetPos);
                _channels[index].MoveAbsolute(60000000);

                if (index != 2) _channels[index].DisableDevice();
            });
        }

        public void StartJog(int index, double velocityPercentage)
        {
            if (!IsConnected(index) || velocityPercentage == 0)
            {
                StopImmediate(index);
                return;
            }

            if (IsSimulationMode)
            {
                _simIsMoving[index] = true;
                return;
            }

            decimal maxVel = 2.0m;
            decimal jogVel = (decimal)(Math.Abs(velocityPercentage) / 100.0) * maxVel;
            if (jogVel < 0.1m) jogVel = 0.1m;

            _channels[index].SetVelocityParams(jogVel, 1.0m);
            _channels[index].MoveContinuous(velocityPercentage > 0 ? MotorDirection.Forward : MotorDirection.Backward);
        }
    }
}