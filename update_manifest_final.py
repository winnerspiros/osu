import xml.etree.ElementTree as ET

file_path = 'osu.Android/AndroidManifest.xml'
android_ns = 'http://schemas.android.com/apk/res/android'
ET.register_namespace('android', android_ns)

tree = ET.parse(file_path)
root = tree.getroot()

application = root.find('application')

# Add Samsung DeX optimizations metadata
dex_metadata = [
    ('com.samsung.android.keepalive.density', 'true'),
    ('com.samsung.android.multidisplay.keep_process_alive', 'true')
]

for name, value in dex_metadata:
    # Check if already exists
    exists = False
    for meta in application.findall('meta-data'):
        if meta.get(f'{{{android_ns}}}name') == name:
            meta.set(f'{{{android_ns}}}value', value)
            exists = True
            break
    if not exists:
        meta = ET.SubElement(application, 'meta-data')
        meta.set(f'{{{android_ns}}}name', name)
        meta.set(f'{{{android_ns}}}value', value)

tree.write(file_path, encoding='utf-8', xml_declaration=True)
