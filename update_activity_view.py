import os

activity_path = 'osu.Android/OsuGameActivity.cs'
with open(activity_path, 'r') as f:
    content = f.read()

# Pass view to handlers after surface is created or in OnCreate
patch = """            mouseHandler = new AndroidMouseHandler();
            Host.AvailableInputHandlers.Add(mouseHandler);
            gameActivity.MouseHandler = mouseHandler;
            mouseHandler.View = Window?.DecorView; // Pass view for coordinate mapping"""

# We need to find where they are instantiated. From previous grep it was OsuGameAndroid.cs
