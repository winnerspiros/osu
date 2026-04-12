with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'r') as f:
    content = f.read()

old_block = '''                                        [
                                            new ShearAligningWrapper(new TitleWedge(beatmap))
                                        {
                                            Shear = -OsuGame.SHEAR,
                                        },
                                        new ShearAligningWrapper(new MetadataWedge(beatmap))
                                        {
                                            Shear = -OsuGame.SHEAR,
                                        },
                                    ]'''

new_block = '''                                        [
                                            new ShearAligningWrapper(new TitleWedge(beatmap))
                                            {
                                                Shear = -OsuGame.SHEAR,
                                            },
                                            new ShearAligningWrapper(new MetadataWedge(beatmap))
                                            {
                                                Shear = -OsuGame.SHEAR,
                                            },
                                        ]'''

content = content.replace(old_block, new_block)
with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'w') as f:
    f.write(content)
