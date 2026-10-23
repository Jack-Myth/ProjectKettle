namespace Kettle.Core;
public static class SteamIdleWait
{
    public static async Task WaitAsync(SteamSession session, uint id, CancellationToken token)
    {
        int empty = 0;
        var stop = DateTime.UtcNow + (TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < stop)
        {
            var remaining = stop - DateTime.UtcNow;
            var queryTimeout = TimeSpan.FromSeconds(8);
            var snapshot = await AppsRunningCommand.ReadAsync(session, remaining < queryTimeout ? remaining : queryTimeout, token);
            // Empty output is the actual observed SteamCMD no-running-app result.
            if (snapshot.Apps.Length == 0)
            {
                if (++empty >= 3)
                    return;
            }
            else
                empty = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }

        throw new IOException(L.Text(TextId.SteamCMDStillReportsTheAppAsBusyCloseThe));
    }

}
public sealed class GameLaunchRejectedException(string message) : SteamCommandExecutionException(message);
