using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NoiseReduction.WinUI
{
    // Тонкий акрил прозрачнее стандартного и сильнее показывает размытый фон
    internal sealed class ThinAcrylic : SystemBackdrop
    {
        private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> controllers = new Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController>();

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            // в светлой теме тонкий акрил над тёмными окнами становится серым, там берём плотный
            var controller = new DesktopAcrylicController { Kind = KindFor(root.Content) };
            if (root.Content is FrameworkElement content) content.ActualThemeChanged += (sender, _) => controller.Kind = KindFor(sender);
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
            controller.AddSystemBackdropTarget(target);
            controllers[target] = controller;
        }

        private static DesktopAcrylicKind KindFor(object content) => content is FrameworkElement element && element.ActualTheme == ElementTheme.Light ? DesktopAcrylicKind.Default : DesktopAcrylicKind.Thin;

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            base.OnTargetDisconnected(target);
            if (!controllers.Remove(target, out var controller)) return;
            controller.RemoveSystemBackdropTarget(target);
            controller.Dispose();
        }
    }
}
