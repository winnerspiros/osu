import os

path = 'osu.Android/OsuGameAndroid.cs'
with open(path, 'r') as f:
    content = f.read()

# Add a field for DeX performance session
if 'private IDisposable? dexPerformanceSession;' not in content:
    content = content.replace('private AndroidHighPerformanceSessionManager? highPerformanceSession;', 'private AndroidHighPerformanceSessionManager? highPerformanceSession;\n        private IDisposable? dexPerformanceSession;')

# Update refresh rate logic to also handle DeX performance session
old_select = """        public void SelectHighestRefreshRate()
        {
            try
            {"""

new_select = """        public void SelectHighestRefreshRate()
        {
            try
            {
                if (gameActivity.IsDeX)
                {
                    if (dexPerformanceSession == null && highPerformanceSession != null)
                    {
                        dexPerformanceSession = highPerformanceSession.BeginSession();
                        Logger.Log("[osu!] Permanent high performance session started for DeX mode.", LoggingTarget.Performance);
                    }
                }
                else
                {
                    dexPerformanceSession?.Dispose();
                    dexPerformanceSession = null;
                }
"""

content = content.replace(old_select, new_select)

with open(path, 'w') as f:
    f.write(content)
