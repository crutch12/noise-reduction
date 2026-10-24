using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NoiseReduction.Desktop;
using NoiseReduction.Platform;

namespace NoiseReduction.WinUI
{
    internal static class MicrophoneText
    {
        internal static string T(string source) => UiText.Get(source).Replace("&", "");

        internal static string StateText(MicrophoneState state) => T(state switch
        {
            MicrophoneState.Paused => "Пауза",
            MicrophoneState.Off => "Выключен",
            MicrophoneState.Detached => "Не подключён к Windows",
            MicrophoneState.Error => "Ошибка обработки",
            MicrophoneState.Active => "Работает",
            _ => "Ждёт звука"
        });

        internal static string Details(DesktopController controller, DeviceInfo device)
        {
            var parts = new List<string> { StateText(controller.StateOf(device.Id)) };
            if (device.Id == controller.DefaultId) parts.Add(T("основной"));
            var apps = controller.Usage(device.Id);
            if (apps != null && apps.Count > 0) parts.Add(string.Join(", ", apps));
            return string.Join(" · ", parts);
        }

        internal static Brush StateBrush(MicrophoneState state)
        {
            string key = state switch
            {
                MicrophoneState.Active or MicrophoneState.Waiting => "SystemFillColorSuccessBrush",
                MicrophoneState.Error => "SystemFillColorCriticalBrush",
                MicrophoneState.Detached => "SystemFillColorCautionBrush",
                _ => "TextFillColorTertiaryBrush"
            };
            return (Brush)Application.Current.Resources[key];
        }
    }
}
