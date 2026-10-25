using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

namespace NoiseReduction.WinUI
{
    internal static class MicrophoneText
    {
        internal static string T(string source) => UiText.Get(source).Replace("&", "");

        internal static void Label(Microsoft.UI.Xaml.Controls.ToggleSwitch toggle)
        {
            toggle.OnContent = T("Вкл");
            toggle.OffContent = T("Откл");
        }

        internal static string StateText(MicrophoneState state) => T(state switch
        {
            MicrophoneState.Paused => "Пауза",
            MicrophoneState.Off => "Выключен",
            MicrophoneState.Detached => "Не работает в программах",
            MicrophoneState.Error => "Ошибка обработки",
            MicrophoneState.Active => "Работает",
            _ => "Ждёт звука"
        });

        internal static string Details(DesktopController controller, DeviceInfo device) => string.Join(" · ", Parts(controller, device));

        // то же самое, но микрофон по умолчанию выделен жирным
        internal static void ShowDetails(TextBlock block, DesktopController controller, DeviceInfo device)
        {
            string marked = T("по умолчанию");
            block.Inlines.Clear();
            foreach (string part in Parts(controller, device))
            {
                if (block.Inlines.Count > 0) block.Inlines.Add(new Run { Text = " · " });
                block.Inlines.Add(device.Id == controller.DefaultId && part == marked ? new Run { Text = part, FontWeight = FontWeights.SemiBold } : new Run { Text = part });
            }
        }

        private static List<string> Parts(DesktopController controller, DeviceInfo device)
        {
            var parts = new List<string> { controller.IntegratingId == device.Id ? T("Включение…") : StateText(controller.StateOf(device.Id)) };
            if (device.Id == controller.DefaultId) parts.Add(T("по умолчанию"));
            var apps = controller.Usage(device.Id);
            if (apps != null && apps.Count > 0) parts.Add(string.Join(", ", apps));
            return parts;
        }

        // цвета Fluent для светлой и тёмной темы, тема берётся у самого элемента, а не у приложения
        internal static Brush StateBrush(MicrophoneState state, FrameworkElement owner)
        {
            bool dark = owner.ActualTheme == ElementTheme.Dark;
            string color = state switch
            {
                MicrophoneState.Active or MicrophoneState.Waiting => dark ? "#6CCB5F" : "#0F7B0F",
                MicrophoneState.Error => dark ? "#FF99A4" : "#C42B1C",
                MicrophoneState.Detached => dark ? "#FCE100" : "#9D5D00",
                _ => dark ? "#9D9D9D" : "#8A8A8A"
            };
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, System.Convert.ToByte(color.Substring(1, 2), 16), System.Convert.ToByte(color.Substring(3, 2), 16), System.Convert.ToByte(color.Substring(5, 2), 16)));
        }
    }
}
