import os

files = ['osu.Android/Linker.xml', 'osu.Android/OsuGameAndroid.cs', 'osu.Android/OboeAudioRedirector.cs']

for f in files:
    print(f"--- {f} ---")
    with open(f, 'r') as content:
        print(content.read())
    print("\n")
