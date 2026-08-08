using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using nanomaxtest.Controllers;
using nanomaxtest.Engines;
using nanomaxtest.Models;

namespace nanomaxtest.Managers
{
    // [모듈: 상태 및 자동화 시퀀스 관리 전담 매니저]
    // UI가 보유하던 전역 컬렉션을 격리하고, 매크로/패턴의 비동기 실행 루프를 독립적으로 구동합니다.
    public class TaskSequenceManager
    {
        private DeviceController _deviceCtrl;
        private MacroEngine _engine;

        public ObservableCollection<MacroCommand> MacroSequence { get; } = new ObservableCollection<MacroCommand>();
        public ObservableCollection<ArrayPoint> ArrayPoints { get; } = new ObservableCollection<ArrayPoint>();

        public volatile bool IsMacroRunning = false;
        public volatile bool IsArrayRunning = false;
        public int CurrentMacroIndex { get; set; } = -1; // 누락된 인덱스 추적 속성 추가
        public int CurrentLoopIndex { get; set; } = 1;

        public double TotalEstimatedPerLoop { get; private set; } = 0;
        public double CompletedTimeInCurrentLoop { get; private set; } = 0;
        private readonly System.Diagnostics.Stopwatch _macroSw = new System.Diagnostics.Stopwatch();

        public void SetTotalEstimatedPerLoop(double seconds)
        {
            TotalEstimatedPerLoop = Math.Max(0, seconds);
        }

        public double GetRemainingTotalSeconds(int totalLoops)
        {

            double currentLoopRemain = Math.Max(0, TotalEstimatedPerLoop - CompletedTimeInCurrentLoop);
            int remainLoops = Math.Max(0, totalLoops - CurrentLoopIndex);
            return currentLoopRemain + (TotalEstimatedPerLoop * remainLoops);
        }

        public event Action<string> NotificationRequested;


        // [모듈: 원형/나선형 패턴 시퀀스 실행기]
        public async Task RunCirclePatternAsync(double startX, double startY, double startZ, double diameter, double zDistPerTurn, double vXY_input, double vZ_input, int steps, int loops, bool zEnabled)
        {
            IsMacroRunning = true;
            bool completedNormally = true;

            try
            {
                double r = diameter / 2.0;
                double centerX = startX - r;
                double centerY = startY;

                // 루프 시작 전에 θ=0 진입점을 명시적으로 먼저 맞춤
                await Task.WhenAll(
                    _deviceCtrl.MoveAbsoluteAsync(0, decimal.Round((decimal)(centerX + r), 5), (decimal)vXY_input, Math.Max((decimal)vXY_input * 20m, 5.0m), true),
                    _deviceCtrl.MoveAbsoluteAsync(1, decimal.Round((decimal)centerY, 5), (decimal)vXY_input, Math.Max((decimal)vXY_input * 20m, 5.0m), true)
                );
                bool enteredX = await _deviceCtrl.WaitUntilStoppedAsync(0, () => !IsMacroRunning);
                bool enteredY = await _deviceCtrl.WaitUntilStoppedAsync(1, () => !IsMacroRunning);
                if (!enteredX || !enteredY)
                {
                    completedNormally = false;
                    return;
                }

                var trajectory = _engine.GenerateSimpleTrajectory(startX, startY, diameter, steps, loops);
                double zStepDist = zDistPerTurn / steps;
                int currentStep = 1;

                foreach (var pt in trajectory)
                {
                    if (!IsMacroRunning)
                    {
                        completedNormally = false;
                        break;
                    }

                    var qx = decimal.Round((decimal)pt.TargetX, 5, MidpointRounding.AwayFromZero);
                    var qy = decimal.Round((decimal)pt.TargetY, 5, MidpointRounding.AwayFromZero);

                    Task taskX = _deviceCtrl.MoveAbsoluteAsync(0, qx, (decimal)vXY_input, Math.Max((decimal)vXY_input * 20m, 5.0m), true);
                    Task taskY = _deviceCtrl.MoveAbsoluteAsync(1, qy, (decimal)vXY_input, Math.Max((decimal)vXY_input * 20m, 5.0m), true);
                    Task taskZ = Task.CompletedTask;

                    if (zEnabled && zDistPerTurn != 0)
                    {
                        double targetZ = startZ + (zStepDist * currentStep);
                        var qz = decimal.Round((decimal)targetZ, 5, MidpointRounding.AwayFromZero);
                        taskZ = _deviceCtrl.MoveAbsoluteAsync(2, qz, (decimal)vZ_input, Math.Max((decimal)vZ_input * 20m, 5.0m), true);
                    }

                    await Task.WhenAll(taskX, taskY, taskZ);
                    bool stepX = await _deviceCtrl.WaitUntilStoppedAsync(0, () => !IsMacroRunning);
                    bool stepY = await _deviceCtrl.WaitUntilStoppedAsync(1, () => !IsMacroRunning);
                    bool stepZ = !zEnabled || zDistPerTurn == 0 || await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsMacroRunning);
                    if (!stepX || !stepY || !stepZ)
                    {
                        completedNormally = false;
                        break;
                    }
                    currentStep++;
                }

                if (zEnabled && zDistPerTurn != 0)
                {
                    bool finalZ = await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsMacroRunning);
                    if (!finalZ) completedNormally = false;
                }

            }
            finally
            {
                IsMacroRunning = false;
                if (completedNormally) NotificationRequested?.Invoke("나선/원형 패턴 그리기가 성공적으로 완료되었습니다.");
            }
        }

        public TaskSequenceManager(DeviceController deviceCtrl, MacroEngine engine)
        {
            _deviceCtrl = deviceCtrl;
            _engine = engine;
        }

        public void StopAll()
        {
            IsMacroRunning = false; IsArrayRunning = false;
            CurrentMacroIndex = -1; CurrentLoopIndex = 1;
            foreach (var cmd in MacroSequence) cmd.HasRuntimeTarget = false; // 캐시 초기화
            // [모듈 수정: 장비 수명 보호 및 링깅(Ringing) 방지] 매크로 취소 시 관성에 의한 장비 무리를 막기 위해, 즉각 정지가 아닌 설정된 가속도 값에 맞춰 부드럽게 감속 정지하도록 수정
            for (int i = 0; i < 3; i++) _deviceCtrl.StopProfiled(i);
        }

        // [모듈: 실시간 기판 기울기가 반영된 매크로 시퀀스 실행 루프]
        public async Task RunMacroSequenceAsync(int totalLoops, bool notifyEveryLoop, bool applySlope = false, double slopeM = 0, int slopeAxis = 0)
        {
            IsMacroRunning = true;
            bool completedNormally = true;
            CompletedTimeInCurrentLoop = 0;
            _macroSw.Restart();
            foreach (var cmd in MacroSequence) cmd.HasRuntimeTarget = false;

            // 보정 기준점 확보: 매크로 시작 시점의 XY 축 현재 좌표를 초기 변위 0점으로 설정
            decimal initialXY = applySlope ? _deviceCtrl.GetPosition(slopeAxis) : 0m;

            for (int loopIndex = 1; loopIndex <= totalLoops; loopIndex++)
            {
                CurrentLoopIndex = loopIndex;
                CompletedTimeInCurrentLoop = 0;
                if (!IsMacroRunning) { completedNormally = false; break; }

                List<Task> batchTasks = new List<Task>();
                List<int> batchAxes = new List<int>();

                for (int i = 0; i < MacroSequence.Count; i++)
                {
                    if (!IsMacroRunning) { completedNormally = false; break; }

                    MacroCommand cmd = MacroSequence[i];
                    CurrentMacroIndex = i; // [모듈: 버그 수정] 현재 진행 인덱스를 갱신하여 UI 시간 표시 연동 복구

                    Task moveTask = Task.CompletedTask;
                    if (cmd.AxisName == "WAIT")
                    {
                        moveTask = Task.Run(async () =>
                        {
                            // [모듈: 매크로 대기 시간 동적 카운트다운] 
                            // 스레드 경계 분리를 위해 UI 스레드 디스패처를 거쳐 프로퍼티를 갱신함으로써 DataGrid 렌더링 누락을 차단합니다.
                            DateTime end = DateTime.Now.AddSeconds(cmd.Target);
                            while (DateTime.Now < end && IsMacroRunning)
                            {
                                double remains = Math.Max(0, (end - DateTime.Now).TotalSeconds);
                                // [모듈 수정: 컴파일러 경고 CS4014 해결] 반환된 Task를 버림(_) 처리하여 UI 갱신을 비동기로 던져둔다는 의도를 명확히 함
                                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => cmd.RemainingTime = remains);
                                await Task.Delay(100);
                            }
                            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => cmd.RemainingTime = 0);
                        });
                    }
                    else
                    {
                        // [모듈: 매크로 이동 시간 동적 카운트다운] 
                        // 일반 구동 모델 역시 크로스 스레드 갱신 오류를 방지하기 위해 일관된 디스패치 구조로 캡슐화합니다.
                        _ = Task.Run(async () => {
                            DateTime end = DateTime.Now.AddSeconds(cmd.EstimatedTime);
                            while (DateTime.Now < end && IsMacroRunning && _deviceCtrl.IsMoving(cmd.AxisId))
                            {
                                double remains = Math.Max(0, (end - DateTime.Now).TotalSeconds);
                                if (Math.Abs(cmd.RemainingTime - remains) > 0.5)
                                    // [모듈 수정: 컴파일러 경고 CS4014 해결] UI 스로틀링 갱신 시에도 동일하게 Discard 연산자 적용
                                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => cmd.RemainingTime = remains);
                                await Task.Delay(100);
                            }
                            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => cmd.RemainingTime = 0);
                        });

                        decimal targetPos = (decimal)cmd.Target;
                        decimal startPos = _deviceCtrl.GetPosition(cmd.AxisId);

                        // [모듈: 일시정지 후 재개 오차 방지] 최초 1회 절대 좌표를 캐싱하여 중단 후 재개 시에도 본래 목적지로 이동
                        if (!cmd.HasRuntimeTarget)
                        {
                            cmd.RuntimeAbsoluteTarget = (double)(cmd.Mode == "Abs" ? targetPos : (startPos + targetPos));
                            cmd.HasRuntimeTarget = true;
                        }
                        decimal finalPos = (decimal)cmd.RuntimeAbsoluteTarget;
                        decimal dist = finalPos - startPos;

                        // [모듈: 기판 기울기 실시간 보정] Z축 구동 시 누적된 XY 변위에 비례하여 높이 자동 합산
                        if (applySlope && cmd.AxisId == 2)
                        {
                            decimal currentXY = _deviceCtrl.GetPosition(slopeAxis);
                            decimal zCorrection = (currentXY - initialXY) * (decimal)slopeM;
                            finalPos += zCorrection;
                        }

                        // [모듈: 기구부 직선 보간 강제] 동시 실행 시 최장 도달 시간(EstimatedTime)을 기준으로 축별 속도를 동기화 보정
                        decimal targetVel = (decimal)cmd.Velocity;
                        if (cmd.IsSync && cmd.EstimatedTime > 0)
                        {
                            decimal syncVel = Math.Abs(dist) / (decimal)cmd.EstimatedTime;
                            if (syncVel > 0) targetVel = syncVel;
                        }

                        // [모듈 수정: .NET 프레임워크 호환성 패치] Math.Clamp가 지원되지 않는 환경을 위해 Math.Max와 Min을 조합하여 동일한 페일세이프(0~4.0mm) 적용
                        finalPos = Math.Max(0m, Math.Min(finalPos, 8.0m));
                        moveTask = _deviceCtrl.MoveAbsoluteAsync(cmd.AxisId, finalPos, targetVel, Math.Max(targetVel * 10m, 0.1m), true);
                    }

                    batchTasks.Add(moveTask);
                    if (cmd.AxisName != "WAIT" && !batchAxes.Contains(cmd.AxisId)) batchAxes.Add(cmd.AxisId);

                    if (!cmd.IsSync || i == MacroSequence.Count - 1)
                    {
                        double batchBilling = 0;
                        foreach (var b in batchTasks) { }
                        for (int k = i; k >= 0; k--)
                        {
                            var c = MacroSequence[k];
                            batchBilling = Math.Max(batchBilling, c.BillingTime);
                            if (!c.IsSync) break;
                        }

                        await Task.WhenAll(batchTasks);
                        bool allStopped = true;
                        foreach (int axis in batchAxes)
                        {
                            bool stopped = await _deviceCtrl.WaitUntilStoppedAsync(axis, () => !IsMacroRunning);
                            allStopped &= stopped;
                        }
                        if (!allStopped) { completedNormally = false; break; }

                        CompletedTimeInCurrentLoop += batchBilling;
                        batchTasks.Clear(); batchAxes.Clear();
                    }

                }

                if (completedNormally && notifyEveryLoop && totalLoops > 1 && loopIndex < totalLoops)
                    NotificationRequested?.Invoke($"매크로 {loopIndex}/{totalLoops}회 반복 완료");
            }

            _macroSw.Stop();
            IsMacroRunning = false;
            CurrentMacroIndex = -1;
            CurrentLoopIndex = 1;
            if (completedNormally) NotificationRequested?.Invoke($"매크로 실행이 완료되었습니다. (총 {totalLoops}회)");
        }


        // [모듈: 어레이 프린팅 시퀀스 루프]
        public async Task RunArrayPrintingAsync(int loops, double printDist, double printVel, double gapDist, double gapVel, double downVel, double slopePerStep, int gapAxis, double gapDir, bool notifyEveryLoop)
        {
            IsArrayRunning = true;
            bool completedNormally = true;

            for (int i = 0; i < loops; i++)
            {
                if (!IsArrayRunning) { completedNormally = false; break; }

                decimal currentZ = _deviceCtrl.GetPosition(2);
                await _deviceCtrl.MoveAbsoluteAsync(2, currentZ - (decimal)printDist, (decimal)printVel, 1m, true);
                await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsArrayRunning);
                await Task.Delay(100);

                if (!IsArrayRunning) { completedNormally = false; break; }
                currentZ = _deviceCtrl.GetPosition(2);
                await _deviceCtrl.MoveAbsoluteAsync(2, currentZ - (decimal)printDist, 1m, 1m, true);
                await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsArrayRunning);
                await Task.Delay(100);

                if (i < loops - 1)
                {
                    if (!IsArrayRunning) { completedNormally = false; break; }
                    decimal currentGapPos = _deviceCtrl.GetPosition(gapAxis);
                    await _deviceCtrl.MoveAbsoluteAsync(gapAxis, currentGapPos + (decimal)(gapDist * gapDir), (decimal)gapVel, 1m, true);
                    await _deviceCtrl.WaitUntilStoppedAsync(gapAxis, () => !IsArrayRunning);
                    await Task.Delay(100);

                    if (slopePerStep != 0)
                    {
                        if (!IsArrayRunning) { completedNormally = false; break; }
                        currentZ = _deviceCtrl.GetPosition(2);
                        double slopeDir = slopePerStep > 0 ? 1.0 : -1.0;
                        await _deviceCtrl.MoveAbsoluteAsync(2, currentZ + (decimal)(Math.Abs(slopePerStep) * slopeDir), 0.1m, 1m, true);
                        await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsArrayRunning);
                        await Task.Delay(100);
                    }

                    if (!IsArrayRunning) { completedNormally = false; break; }
                    currentZ = _deviceCtrl.GetPosition(2);
                    await _deviceCtrl.MoveAbsoluteAsync(2, currentZ + (decimal)(printDist * 2), (decimal)downVel, 1m, true);
                    await _deviceCtrl.WaitUntilStoppedAsync(2, () => !IsArrayRunning);
                    await Task.Delay(100);
                }

                if (notifyEveryLoop && loops > 1) NotificationRequested?.Invoke($"어레이 {i + 1}/{loops}번째 기둥 완성");
            }

            IsArrayRunning = false;
            if (completedNormally) NotificationRequested?.Invoke($"어레이 작업이 최종 완료되었습니다. (총 {loops}개)");
        }
    }
}
