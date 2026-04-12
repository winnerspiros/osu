with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'r') as f:
    content = f.read()

# Bot mentioned: "InspectCode / Incorrect line breaks: Line break is missing elsewhere"
# Re-evaluating the Children = ... [ line.
# It might want the [ on the next line or indented differently.

old_ternary = '''                                    Children = beatmap == null
                                        ? System.Array.Empty<Drawable>()
                                        : [
                                        new ShearAligningWrapper(new TitleWedge(beatmap))'''

new_ternary = '''                                    Children = beatmap == null
                                        ? System.Array.Empty<Drawable>()
                                        :
                                        [
                                            new ShearAligningWrapper(new TitleWedge(beatmap))'''

content = content.replace(old_ternary, new_ternary)
with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'w') as f:
    f.write(content)
