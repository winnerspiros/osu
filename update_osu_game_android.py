file_path = 'osu.Android/OsuGameAndroid.cs'
with open(file_path, 'r') as f:
    content = f.read()

content = content.replace('gameActivity.StylusHandler = stylusHandler;', 'gameActivity.StylusHandler = stylusHandler;\n            stylusHandler.View = gameActivity.Window?.DecorView;')
content = content.replace('gameActivity.MouseHandler = mouseHandler;', 'gameActivity.MouseHandler = mouseHandler;\n            mouseHandler.View = gameActivity.Window?.DecorView;')

with open(file_path, 'w') as f:
    f.write(content)
