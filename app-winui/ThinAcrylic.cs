using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NoiseReduction.WinUI
{
    // Акрил с размытым фоном, плотность подобрана под читаемость текста
    internal sealed class ThinAcrylic : SystemBackdrop
    {
        private readonly bool dense;
        internal ThinAcrylic(bool dense = false) { this.dense = dense; }
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

        private DesktopAcrylicKind KindFor(object content) => dense || content is FrameworkElement element && element.ActualTheme == ElementTheme.Light ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Default;

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            base.OnTargetDisconnected(target);
            if (!controllers.Remove(target, out var controller)) return;
            controller.RemoveSystemBackdropTarget(target);
            controller.Dispose();
        }
    }
}
