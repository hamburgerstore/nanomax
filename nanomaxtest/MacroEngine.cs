using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using nanomaxtest.Models;

namespace nanomaxtest.Engines
{
    public class MacroEngine
    {
        // [모듈: 원형/나선형 궤적 및 XY 속도 연산]
        public double CalculateHelixVelocity(double diameter, double zDistPerTurn, double vZ, int steps, int loops)
        {
            double totalZDist = Math.Abs(zDistPerTurn * loops);
            if (totalZDist == 0 || vZ <= 0 || steps < 4 || loops < 1 || diameter <= 0) return 0;

            double r = diameter / 2.0;
            double angleStep = 2 * Math.PI / steps;
            double chordLength = 2 * r * Math.Sin(angleStep / 2.0);
            double totalPathXY = chordLength * steps * loops;

            double xyTime = totalZDist / vZ;
            return totalPathXY / xyTime;
        }

        // 원 중심을 명시적으로 정의하고 루프 단위로 닫힌 궤적을 생성
        public List<TrajectoryPoint> GenerateSimpleTrajectory(double approachX, double approachY, double diameter, int steps, int loops)
        {
            var trajectory = new List<TrajectoryPoint>();
            if (steps < 4 || loops < 1 || diameter <= 0) return trajectory;

            double r = diameter / 2.0;
            double centerX = approachX - r;
            double centerY = approachY;

            for (int lp = 0; lp < loops; lp++)
            {
                for (int s = 1; s <= steps; s++)
                {
                    double theta = (2.0 * Math.PI * s / steps) + (2.0 * Math.PI * lp);
                    trajectory.Add(new TrajectoryPoint
                    {
                        TargetX = centerX + r * Math.Cos(theta),
                        TargetY = centerY + r * Math.Sin(theta)
                    });
                }
            }
            return trajectory;
        }

        // [모듈: 3D 헬릭스 궤적 연산] 
        // 팁 파손을 방지하는 Z축 하강 벡터(음수 강제)를 포함하여 3차원 나선형 좌표를 일괄 산출합니다.
        public List<TrajectoryPoint> GenerateHelixTrajectory(double approachX, double approachY, double startZ, double diameter, double zDistPerTurn, int steps, int loops)
        {
            var trajectory = new List<TrajectoryPoint>();
            if (steps < 4 || loops < 1 || diameter <= 0) return trajectory;

            double r = diameter / 2.0;
            double centerX = approachX - r;
            double actualZDistPerTurn = -Math.Abs(zDistPerTurn);
            double zStep = actualZDistPerTurn / steps;

            for (int lp = 0; lp < loops; lp++)
            {
                for (int s = 1; s <= steps; s++)
                {
                    double theta = (2.0 * Math.PI * s / steps) + (2.0 * Math.PI * lp);
                    trajectory.Add(new TrajectoryPoint
                    {
                        TargetX = centerX + r * Math.Cos(theta),
                        TargetY = approachY + r * Math.Sin(theta),
                        TargetZ = startZ + zStep * (s + lp * steps),
                        MoveX = true,
                        MoveY = true,
                        MoveZ = true
                    });
                }
            }
            return trajectory;
        }

        // [모듈: 어레이 프린팅 기울기 도출 (최소제곱법)]
        public double CalculateArraySlope(IEnumerable<ArrayPoint> points, double gapDist, int gapDirIndex)
        {
            int n = 0;
            double sumX = 0, sumZ = 0, sumXZ = 0, sumX2 = 0;
            foreach (var pt in points)
            {
                n++;
                sumX += pt.AxisPos;
                sumZ += pt.ZPos;
                sumXZ += pt.AxisPos * pt.ZPos;
                sumX2 += pt.AxisPos * pt.AxisPos;
            }

            if (n == 0) return 0;
            double denominator = n * sumX2 - sumX * sumX;
            double m = denominator == 0 ? 0 : (n * sumXZ - sumX * sumZ) / denominator;

            double gapDir = gapDirIndex == 0 ? 1.0 : -1.0;
            return m * gapDist * gapDir;
        }

        // [모듈: 기판 순수 기울기 산출 (mm/mm)]
        public double CalculatePureSlope(IEnumerable<ArrayPoint> points)
        {
            int n = 0;
            double sumX = 0, sumZ = 0, sumXZ = 0, sumX2 = 0;
            foreach (var pt in points)
            {
                n++; sumX += pt.AxisPos; sumZ += pt.ZPos;
                sumXZ += pt.AxisPos * pt.ZPos; sumX2 += pt.AxisPos * pt.AxisPos;
            }
            if (n == 0) return 0;
            double denominator = n * sumX2 - sumX * sumX;
            return denominator == 0 ? 0 : (n * sumXZ - sumX * sumZ) / denominator;
        }
        // [모듈: 각도 기반 좌표/속도 변환 연산]
        public AngleMoveData CalculateAngleMovement(double angle, double dist, double vel)
        {
            double rad = angle * Math.PI / 180.0;
            double dx = dist * Math.Cos(rad);
            double dy = dist * Math.Sin(rad);

            double vx = Math.Abs(dx) > 0 ? vel * Math.Abs(Math.Cos(rad)) : 0;
            double vy = Math.Abs(dy) > 0 ? vel * Math.Abs(Math.Sin(rad)) : 0;

            return new AngleMoveData
            {
                DistanceX = Math.Abs(dx),
                DistanceY = Math.Abs(dy),
                VelX = vx < 0.00001 ? 0 : vx,
                VelY = vy < 0.00001 ? 0 : vy,
                DirX = dx > 0 ? 1 : -1,
                DirY = dy > 0 ? 1 : -1
            };
        }

        // [모듈: 매크로 시퀀스 예상 시간 계산]
        public void CalculateMacroEstimatedTimes(IEnumerable<MacroCommand> sequence, double[] currentPositions)
        {
            double[] simPos = (double[])currentPositions.Clone();
            var list = new System.Collections.Generic.List<MacroCommand>(sequence);
            int i = 0;

            while (i < list.Count)
            {
                var batch = new System.Collections.Generic.List<MacroCommand>();
                batch.Add(list[i]);
                while (list[i].IsSync && i + 1 < list.Count)
                {
                    i++;
                    batch.Add(list[i]);
                }

                double maxTime = 0;
                double dx = 0, dy = 0, dz = 0;
                double sumVelSq = 0;

                // 1차 변위 누적 갱신 및 개별 요구시간 산출
                foreach (var cmd in batch)
                {
                    if (cmd.AxisName == "WAIT")
                    {
                        cmd.EstimatedTime = cmd.Target;
                        cmd.RemainingTime = cmd.Target;
                        cmd.SyncSummary = "대기";
                        maxTime = System.Math.Max(maxTime, cmd.Target);
                        continue;
                    }

                    double dist = cmd.Mode == "Abs" ? (cmd.Target - simPos[cmd.AxisId]) : cmd.Target;
                    simPos[cmd.AxisId] += dist;

                    if (cmd.AxisId == 0) dx += dist;
                    else if (cmd.AxisId == 1) dy += dist;
                    else if (cmd.AxisId == 2) dz += dist;

                    double v = cmd.Velocity;
                    if (v > 0) sumVelSq += v * v;

                    double t = v > 0 ? System.Math.Abs(dist) / v : 0;
                    cmd.EstimatedTime = t;
                    cmd.RemainingTime = t; // [모듈: 버그 수정] 시작 전 모든 명령의 초기 남은 시간 세팅
                    maxTime = System.Math.Max(maxTime, t);
                }

                // [모듈 수정: 다축 동기화(Interpolation) 궤적 붕괴 해결] 동기화 그룹 내의 모든 축이 가장 오래 걸리는 시간(maxTime)에 맞춰 도착하도록 목표 시간을 통일하여 완전한 직선 궤적 보장
                if (batch.Count > 1)
                {
                    bool billed = false;
                    foreach (var cmd in batch)
                    {
                        if (cmd.AxisName == "WAIT")
                        {
                            cmd.BillingTime = cmd.Target;
                            continue;
                        }
                        cmd.EstimatedTime = cmd.RemainingTime = maxTime;
                        cmd.BillingTime = billed ? 0 : maxTime;
                        billed = true;
                    }
                }
                else
                {
                    var only = batch[0];
                    only.BillingTime = only.EstimatedTime;
                }

                // 2차 합성 속도 V = sqrt(vx^2 + vy^2 + vz^2) 및 실제 구동 각도 도출

                double resultantVel = System.Math.Sqrt(sumVelSq);
                string angleStr = "0.0°";
                int movingAxesCount = 0;

                if (System.Math.Abs(dx) > 1e-9) movingAxesCount++;
                if (System.Math.Abs(dy) > 1e-9) movingAxesCount++;
                if (System.Math.Abs(dz) > 1e-9) movingAxesCount++;

                if (movingAxesCount >= 2)
                {
                    if (System.Math.Abs(dx) > 1e-9 && System.Math.Abs(dz) > 1e-9)
                    {
                        double angle = System.Math.Atan2(System.Math.Abs(dz), System.Math.Abs(dx)) * 180.0 / System.Math.PI;
                        angleStr = $"{angle:F1}° (XZ평면)";
                    }
                    else if (System.Math.Abs(dx) > 1e-9 && System.Math.Abs(dy) > 1e-9)
                    {
                        double angle = System.Math.Atan2(System.Math.Abs(dy), System.Math.Abs(dx)) * 180.0 / System.Math.PI;
                        angleStr = $"{angle:F1}° (XY평면)";
                    }
                    else if (System.Math.Abs(dy) > 1e-9 && System.Math.Abs(dz) > 1e-9)
                    {
                        double angle = System.Math.Atan2(System.Math.Abs(dz), System.Math.Abs(dy)) * 180.0 / System.Math.PI;
                        angleStr = $"{angle:F1}° (YZ평면)";
                    }
                }
                else if (movingAxesCount == 1)
                {
                    angleStr = "직선(축단독)";
                }

                foreach (var cmd in batch)
                {
                    if (cmd.AxisName != "WAIT")
                    {
                        cmd.SyncSummary = batch.Count > 1
                            ? $"최종 합성 속도: {resultantVel:F4} | 이동 각도: {angleStr}"
                            : "단독 구동";
                    }
                }

                i++;
            }
        }

        // [모듈: CSV 파싱 데이터 변환]
        public List<MacroCommand> ParseMacroCsv(string filePath)
        {
            var parsedSequence = new List<MacroCommand>();
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] cols = lines[i].Split(',');
                if (cols.Length >= 5)
                {
                    MacroCommand cmd = new MacroCommand();
                    if (int.TryParse(cols[0], out int parsedIndex)) cmd.Index = parsedIndex;
                    else cmd.Index = parsedSequence.Count + 1;

                    cmd.AxisName = cols[1].Trim().ToUpper();
                    if (cmd.AxisName == "X" || cmd.AxisName == "1") cmd.AxisId = 0;
                    else if (cmd.AxisName == "Y" || cmd.AxisName == "2") cmd.AxisId = 1;
                    else if (cmd.AxisName == "Z" || cmd.AxisName == "3") cmd.AxisId = 2;
                    else if (cmd.AxisName == "WAIT" || cmd.AxisName == "W") cmd.AxisId = 3;
                    else continue;

                    // [모듈 수정: 하드웨어 충돌 방지] 대소문자("ABS", "abs") 구분 오작동으로 인해 절대 좌표 이동이 상대 좌표(Rel)로 변환되어 장비 리미트 충돌이 발생하는 현상 원천 차단
                    cmd.Mode = cols[2].Trim().Equals("Abs", StringComparison.OrdinalIgnoreCase) ? "Abs" : "Rel";

                    if (double.TryParse(cols[3], out double target) && double.TryParse(cols[4], out double vel))
                    {
                        cmd.Target = target;
                        cmd.Velocity = vel;
                        cmd.IsSync = cols.Length >= 6 && (cols[5].Trim().ToUpper() == "O" || cols[5].Trim().ToUpper() == "TRUE");
                        parsedSequence.Add(cmd);
                    }
                }
            }
            return parsedSequence;
        }
    }
}