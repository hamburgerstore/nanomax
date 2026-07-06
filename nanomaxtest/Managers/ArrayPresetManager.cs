using System.Collections.Generic;
using System.IO;
using System.Text;

namespace nanomaxtest.Managers
{
    // [모듈: 메모리 캐싱을 적용한 어레이 프리셋 전담] 불필요한 반복 파일 I/O 제거
    public class ArrayPresetManager
    {
        private readonly string _presetFilePath;
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();

        public ArrayPresetManager(string appDataPath)
        {
            _presetFilePath = Path.Combine(appDataPath, "NanoMax_ArrayPresets.txt");
            if (File.Exists(_presetFilePath))
            {
                foreach (string line in File.ReadAllLines(_presetFilePath, Encoding.UTF8))
                {
                    if (!string.IsNullOrWhiteSpace(line)) _cache[line.Split('|')[0]] = line;
                }
            }
        }

        // [모듈 수정: 파일 시스템 페일세이프] AppData 폴더 내 대상 디렉토리가 존재하지 않을 경우 발생하는 DirectoryNotFoundException(앱 크래시) 방지
        private void SaveCache()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_presetFilePath));
            File.WriteAllLines(_presetFilePath, _cache.Values, Encoding.UTF8);
        }

        public List<string> GetPresetNames() => new List<string>(_cache.Keys);

        public void SavePreset(string name, string dataLine)
        {
            _cache[name] = dataLine;
            SaveCache();
        }

        public void DeletePreset(string name)
        {
            if (_cache.Remove(name)) SaveCache();
        }

        public string[] LoadPresetData(string name)
        {
            return _cache.TryGetValue(name, out string dataLine) ? dataLine.Split('|') : null;
        }
    }
}