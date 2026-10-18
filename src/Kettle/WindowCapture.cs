using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Kettle;

internal static class WindowCapture
{
    public static void Attach(Window window, string path, bool close)
    {
        window.Opened += async (_, _) =>
        {
            await Task.Delay(600);
            try
            {
                var full = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                image.Render(window);
                image.Save(full);
            }
            catch
            {
                Environment.ExitCode = 3;
            }

            if (close)
                window.Close();
        };
    }
}
