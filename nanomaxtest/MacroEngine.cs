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

        // [모듈: Catmull-Rom Spline 기반 등속(Constant Velocity) 연속 궤적 생성기]
        // 속도 벡터의 크기(|v|)를 상수로 유지하기 위해 미소 길이(ds) 단위로 곡선을 보간합니다.
        public List<TrajectoryPoint> GenerateSimpleTrajectory(double startX, double startY, double diameter, int steps, int loops)
        {
            var trajectory = new List<TrajectoryPoint>();
            double r = diameter / 2.0;
            for (int i = 1; i <= (steps * loops); i++)
            {
                double angle = 2 * Math.PI * i / steps;
                trajectory.Add(new TrajectoryPoint
                {
                    TargetX = startX + r * Math.Cos(angle) - r,
                    TargetY = startY + r * Math.Sin(angle)
                });
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
                    maxTime = System.Math.Max(maxTime, t);
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

                    cmd.Mode = cols[2].Trim() == "Abs" ? "Abs" : "Rel";

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