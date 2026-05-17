#!/bin/bash
BASE="/home/runner/work/osu/osu"
cd "$BASE"

FIXED=0

# Find ALL .cs files in osu.Game that have bare "using osuTK;" and use Vector2
while IFS= read -r FULLPATH; do
  # Skip if already fixed
  if grep -qP 'using Vector2 = System\.Numerics\.Vector2;\r?$' "$FULLPATH"; then
    continue
  fi
  if grep -qP '^using System\.Numerics;\r?$' "$FULLPATH"; then
    continue
  fi
  
  # Check if uses Vector2
  if ! grep -qP '\bVector2\b' "$FULLPATH"; then
    continue
  fi
  
  # Determine if Color4 or Vector3/Vector4 needs osuTK namespace
  HAS_OSUTK_GRAPHICS=$(grep -cP '^using osuTK\.Graphics;\r?$' "$FULLPATH" || true)
  HAS_COLOR4=$(grep -c 'Color4' "$FULLPATH" || true)
  HAS_VECTOR3=$(grep -cP '\bVector3\b' "$FULLPATH" || true)
  HAS_VECTOR4=$(grep -cP '\bVector4\b' "$FULLPATH" || true)
  
  NEEDS_OSUTK=0
  if [ "$HAS_VECTOR3" -gt 0 ] || [ "$HAS_VECTOR4" -gt 0 ]; then
    NEEDS_OSUTK=1
  fi
  if [ "$HAS_COLOR4" -gt 0 ] && [ "$HAS_OSUTK_GRAPHICS" -eq 0 ]; then
    NEEDS_OSUTK=1
  fi
  
  if [ "$NEEDS_OSUTK" -eq 1 ]; then
    perl -i -0pe 's/(using osuTK;)(\r?\n)/\1\2using Vector2 = System.Numerics.Vector2;\2/' "$FULLPATH"
    echo "FIXED (alias): $FULLPATH"
  else
    perl -i -pe 's/^using osuTK;\r?\n/using System.Numerics;\n/' "$FULLPATH"
    echo "FIXED (replaced): $FULLPATH"
  fi
  ((FIXED++))
done < <(grep -rP '^using osuTK;\r?$' osu.Game/ --include="*.cs" -l)

echo ""
echo "Total fixed: $FIXED"
