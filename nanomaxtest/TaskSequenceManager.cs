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

        public event Action<string> NotificationRequested;

        // [모듈: 원형/나선형 패턴 시퀀스 실행기] 아래 메서드를 클래스 내부에 통째로 추가하세요.
        // [모듈: 원형/나선형 패턴 시퀀스 실행기]
        public async Task RunCirclePatternAsync(double startX, double startY, double startZ, double diameter, double zDistPerTurn, double vXY_input, double vZ_input, int steps, int loops, bool zEnabled)
        {
            IsMacroRunning = true;
            bool completedNormally = true;

            try
            {
                double totalZDist = zDistPerTurn * loops;
                Task zTask = Task.CompletedTask;
                if (zEnabled && totalZDist != 0)
                {
                    zTask = _deviceCtrl.MoveAbsoluteAsync(2, (decimal)(startZ + totalZDist), (decimal)vZ_input, Math.Max((decimal)vZ_input * 10m, 0.1m), true);
                }

                var trajectory = _engine.GenerateSimpleTrajectory(startX, startY, diameter, steps, loops);

                foreach (var pt in trajectory)
                {
                    if (!IsMacroRunning) break;

                    // [모듈: 동시 다축 구동] X, Y축을 비동기로 동시 출발시켜 계단식 궤적(Zig-zag) 및 메니스커스 파괴 방지
                    Task taskX = _deviceCtrl.MoveAbsoluteAsync(0, (decimal)pt.TargetX, (decimal)vXY_input, 1.0m, true);
                    Task taskY = _deviceCtrl.MoveAbsoluteAsync(1, (decimal)pt.TargetY, (decimal)vXY_input, 1.0m, true);

                    await Task.WhenAll(taskX, taskY);

                    Task waitX = _deviceCtrl.WaitUntilStoppedAsync(0, () => !IsMacroRunning);
                    Task waitY = _deviceCtrl.WaitUntilStoppedAsync(1, () => !IsMacroRunning);

                    await Task.WhenAll(waitX, waitY);
                }

                if (zEnabled && totalZDist != 0)
                {
                    try { await zTask; } catch { }
                    await _deviceCtrl.WaitUntilStoppedAsync(2);
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
            IsMacroRunning = false;
            IsArrayRunning = false;
            for (int i = 0; i < 3; i++) _deviceCtrl.StopImmediate(i);
        }

        // [모듈: 실시간 기판 기울기가 반영된 매크로 시퀀스 실행 루프]
        public async Task RunMacroSequenceAsync(int totalLoops, bool notifyEveryLoop, bool applySlope = false, double slopeM = 0, int slopeAxis = 0)
        {
            IsMacroRunning = true;
            bool completedNormally = true;

            // 보정 기준점 확보: 매크로 시작 시점의 XY 축 현재 좌표를 초기 변위 0점으로 설정
            decimal initialXY = applySlope ? _deviceCtrl.GetPosition(slopeAxis) : 0m;

            for (int loopIndex = 1; loopIndex <= totalLoops; loopIndex++)
            {
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
                            int waitMs = (int)(cmd.Target * 1000);
                            DateTime endTime = DateTime.Now.AddMilliseconds(waitMs);
                            while (DateTime.Now < endTime && IsMacroRunning) await Task.Delay(50);
                        });
                    }
                    else
                    {
                        decimal targetPos = (decimal)cmd.Target;
                        decimal startPos = _deviceCtrl.GetPosition(cmd.AxisId);
                        decimal dist = cmd.Mode == "Abs" ? (targetPos - startPos) : targetPos;
                        decimal finalPos = cmd.Mode == "Abs" ? targetPos : (startPos + targetPos);

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

                        moveTask = _deviceCtrl.MoveAbsoluteAsync(cmd.AxisId, finalPos, targetVel, Math.Max(targetVel * 10m, 0.1m), true);
                    }

                    batchTasks.Add(moveTask);
                    if (cmd.AxisName != "WAIT" && !batchAxes.Contains(cmd.AxisId)) batchAxes.Add(cmd.AxisId);

                    if (!cmd.IsSync || i == MacroSequence.Count - 1)
                    {
                        await Task.WhenAll(batchTasks);
                        foreach (int axis in batchAxes) await _deviceCtrl.WaitUntilStoppedAsync(axis, () => !IsMacroRunning);
                        batchTasks.Clear(); batchAxes.Clear();
                        await Task.Delay(200);
                    }
                }

                if (completedNormally && notifyEveryLoop && totalLoops > 1 && loopIndex < totalLoops)
                    NotificationRequested?.Invoke($"매크로 {loopIndex}/{totalLoops}회 반복 완료");
            }

            IsMacroRunning = false;
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