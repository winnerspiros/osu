import xml.etree.ElementTree as ET
import sys

file_path = 'osu.Android/AndroidManifest.xml'
android_ns = 'http://schemas.android.com/apk/res/android'
ET.register_namespace('android', android_ns)

tree = ET.parse(file_path)
root = tree.getroot()

application = root.find('application')

# Add/Update Activity properties
# Since Activity isn't in this manifest directly (it might be in a base or dynamically added),
# but let's try to find it or add a generic one if we can't find it.
# Actually, osu!lazer Android manifest usually has the activity. Let me check the cat output again.
