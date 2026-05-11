using System;
using System.ComponentModel;

namespace nanomaxtest.Models
{
    // [모듈: 공통 바인딩 모델] INotifyPropertyChanged 중복 방지를 위한 추상 클래스
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MacroCommand : ObservableObject
    {
        private int _index;
        public int Index { get => _index; set { _index = value; OnPropertyChanged(nameof(Index)); } }

        private string _axisName;
        public string AxisName
        {
            get => _axisName;
            set
            {
                _axisName = value?.Trim().ToUpper();
                if (_axisName == "X" || _axisName == "1") AxisId = 0;
                else if (_axisName == "Y" || _axisName == "2") AxisId = 1;
                else if (_axisName == "Z" || _axisName == "3") AxisId = 2;
                else if (_axisName == "WAIT" || _axisName == "W") AxisId = 3;
                OnPropertyChanged(nameof(AxisName));
            }
        }

        public int AxisId { get; set; }

        private string _mode;
        public string Mode { get => _mode; set { _mode = value; OnPropertyChanged(nameof(Mode)); } }

        private double _target;
        public double Target { get => _target; set { _target = value; OnPropertyChanged(nameof(Target)); } }

        private double _velocity;
        public double Velocity { get => _velocity; set { _velocity = value; OnPropertyChanged(nameof(Velocity)); } }

        private bool _isSync;
        public bool IsSync { get => _isSync; set { _isSync = value; OnPropertyChanged(nameof(IsSync)); } }

        private double _estimatedTime;
        public double EstimatedTime { get => _estimatedTime; set { _estimatedTime = value; OnPropertyChanged(nameof(EstimatedTime)); } }

        private double _remainingTime;
        public double RemainingTime { get => _remainingTime; set { _remainingTime = value; OnPropertyChanged(nameof(RemainingTime)); } }

        private string _syncSummary;
        public string SyncSummary { get => _syncSummary; set { _syncSummary = value; OnPropertyChanged(nameof(SyncSummary)); } }
    }

    public class ArrayPoint : ObservableObject
    {
        private double _axisPos;
        public double AxisPos { get => _axisPos; set { _axisPos = value; OnPropertyChanged(nameof(AxisPos)); } }

        private double _zPos;
        public double ZPos { get => _zPos; set { _zPos = value; OnPropertyChanged(nameof(ZPos)); } }
    }

    public class TrajectoryPoint
    {
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double VelX { get; set; }
        public double VelY { get; set; }
        public bool MoveX { get; set; }
        public bool MoveY { get; set; }
    }

    public class AngleMoveData
    {
        public double DistanceX { get; set; }
        public double DistanceY { get; set; }
        public double VelX { get; set; }
        public double VelY { get; set; }
        public int DirX { get; set; }
        public int DirY { get; set; }
    }
}