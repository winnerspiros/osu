import sys

def fix_file(path, required_usings):
    with open(path, 'r') as f:
        lines = f.readlines()

    # Separate usings and body
    usings = []
    body = []
    in_usings = True
    for line in lines:
        if in_usings and (line.startswith('using ') or line.startswith('#nullable') or not line.strip()):
            if line.startswith('using '):
                usings.append(line)
        else:
            in_usings = False
            body.append(line)

    # Deduplicate and sort usings, keeping only required ones plus existing ones
    # (In this case we just want to deduplicate and ensure necessary ones are there)
    seen = set()
    unique_usings = []
    for u in usings:
        if u not in seen:
            unique_usings.append(u)
            seen.add(u)

    # Ensure required usings are present
    for req in required_usings:
        req_line = f"using {req};\n"
        if req_line not in seen:
            unique_usings.append(req_line)
            seen.add(req_line)

    with open(path, 'w') as f:
        f.writelines(unique_usings + body)

fix_file('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs',
         ['osu.Game.Rulesets.Judgements', 'osu.Game.Rulesets.Osu.Judgements'])
