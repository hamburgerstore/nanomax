using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace nanomaxtest.Managers
{
    // [모듈: 파일 입출력 전담 매니저] CSV 다운로드, 로그 및 건의함 저장 등 디스크 접근 로직 격리
    public class FileManager
    {
        public void ExportLog(List<string> actionLog)
        {
            try
            {
                // [모듈 수정: 아키텍처 및 크로스 스레드 크래시 방지] 백그라운드 태스크 완료 후 호출 시 STA 위반 예외를 막기 위해 UI 디스패처로 캡슐화
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv|텍스트 파일 (*.txt)|*.txt", FileName = $"NanoMax_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
                    if (sfd.ShowDialog() == true)
                    {
                        File.WriteAllLines(sfd.FileName, actionLog, Encoding.UTF8);
                        MessageBox.Show("로그가 성공적으로 저장되었습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                });
            }
            catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"로그 저장 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error)); }
        }

        public void DownloadCsvTemplate()
        {
            try
            {
                SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = "기본 틀.csv" };
                if (sfd.ShowDialog() == true)
                {
                    StringBuilder sb = new StringBuilder();
                    // [모듈: 엑셀 열 분리 오류 해결] 헤더 텍스트 내부의 쉼표를 슬래시로 변경하여 셀 밀림 현상을 방지합니다.
                    sb.AppendLine("순서,축(X/Y/Z/WAIT),모드(Abs/Rel),좌표/거리/시간,이동 속도,동시실행(O/X)");
                    sb.AppendLine("1,X,Abs,1.5,0.5,O");
                    sb.AppendLine("2,Y,Abs,1.5,0.5,X");
                    sb.AppendLine("3,Z,Rel,0.1,0.05,X");
                    sb.AppendLine("4,WAIT,None,5.0,0.0,X");
                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show("기본 틀 다운로드가 완료되었습니다.\n엑셀에서 열어서 수정 후 사용하세요.", "다운로드 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"다운로드 실패:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // [모듈: 파일 입출력 전담] UI 종속 객체(MessageBox)를 제거하고 예외 처리 위임
        public void SaveFeedback(string appDataPath, string name, string content)
        {
            string filePath = Path.Combine(appDataPath, "NanoMax_Feedback.txt");
            // [모듈 수정: 파일 시스템 페일세이프] AppData 폴더가 존재하지 않을 시 발생하는 DirectoryNotFoundException(앱 크래시) 방지를 위해 강제 생성
            Directory.CreateDirectory(appDataPath);
            string feedbackText = $"--- 작성자: {name} | 작성일시: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n{content}\n\n";
            File.AppendAllText(filePath, feedbackText, Encoding.UTF8);
        }

        // [모듈: 어레이 매크로 CSV 추출 전담] UI에서 텍스트 조립 로직을 분리
        public void ExportArrayMacroCsv(int loops, double printDist, double printVel, double gapDist, double gapVel, double downVel, double slopePerStep, string axisName, double gapDir)
        {
            try
            {
                SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = $"ArrayMacro_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
                if (sfd.ShowDialog() == true)
                {
                    StringBuilder sb = new StringBuilder();
                    // [모듈 수정: 데이터 정합성 결함 해결] 누락된 '순서' 헤더를 추가하여 엑셀에서 열거나 재파싱 시 컬럼이 어긋나는 현상 방지
                    sb.AppendLine("순서,축(x,y,z,wait),모드(Abs/Rel),좌표/거리/시간,이동 속도,동시실행(O/X)");
                    int stepIdx = 1;

                    for (int i = 0; i < loops; i++)
                    {
                        sb.AppendLine($"{stepIdx++},Z,Rel,{-printDist:0.######},{printVel:0.######},X");
                        sb.AppendLine($"{stepIdx++},Z,Rel,{-printDist:0.######},1.0,X");

                        if (i < loops - 1)
                        {
                            sb.AppendLine($"{stepIdx++},{axisName},Rel,{gapDir * gapDist:0.######},{gapVel:0.######},X");
                            if (slopePerStep != 0) sb.AppendLine($"{stepIdx++},Z,Rel,{slopePerStep:0.######},0.1,X");
                            sb.AppendLine($"{stepIdx++},Z,Rel,{(printDist * 2):0.######},{downVel:0.######},X");
                        }
                    }

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show("상대 좌표 기준의 매크로용 CSV 파일이 성공적으로 추출되었습니다.\n'매크로' 탭에서 불러오기 버튼을 일 통해 실행할 수 있습니다.", "추출 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"파일 추출 중 오류 발생:\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}