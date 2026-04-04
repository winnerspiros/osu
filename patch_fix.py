import sys

path = 'build/PatchElfPageSize.targets'
with open(path, 'r') as f:
    content = f.read()

old_code = """    System.IO.File.WriteAllBytes(FilePath, newData);
    WasPatched = true;"""

new_code = """    // Even with a lock file, the actual .so may be locked for reading by another MSBuild process.
    // Retry writing the actual file.
    for (int attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            System.IO.File.WriteAllBytes(FilePath, newData);
            WasPatched = true;
            break;
        }
        catch (System.IO.IOException)
        {
            if (attempt == 29) throw;
            System.Threading.Thread.Sleep(1000);
        }
    }"""

if old_code in content:
    content = content.replace(old_code, new_code)
    with open(path, 'w') as f:
        f.write(content)
    print("Patched PatchElfPageSize.targets")
else:
    print("Could not find code block in PatchElfPageSize.targets")
