using System.Windows;
using System.Windows.Controls;

namespace nanomaxtest
{
    public partial class AxisControl : UserControl
    {
        public AxisControl()
        {
            InitializeComponent();
        }

        // MainWindow의 XAML에서 Title="..."을 직접 입력할 수 있도록 허용하는 의존성 속성
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(AxisControl),
            new PropertyMetadata("축 제어", OnTitleChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AxisControl control && control.txtTitle != null)
            {
                control.txtTitle.Text = e.NewValue?.ToString();
            }
        }

        private void cmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lblTarget == null) return;

            if (cmbMode.SelectedIndex == 0) // 상대 좌표
            {
                lblTarget.Text = "이동 거리:";
                btnMinus.Visibility = Visibility.Visible;
                btnPlus.Visibility = Visibility.Visible;
                btnGo.Visibility = Visibility.Collapsed;
            }
            else // 절대 좌표
            {
                lblTarget.Text = "목표 위치:";
                btnMinus.Visibility = Visibility.Collapsed;
                btnPlus.Visibility = Visibility.Collapsed;
                btnGo.Visibility = Visibility.Visible;
            }
        }
    }
}