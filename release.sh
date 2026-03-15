#!/bin/bash

# Check if a version argument was provided
if [ -z "$1" ]; then
  echo "Usage: ./release.sh vX.X.X.X"
  exit 1
fi

VERSION=$1
SOURCE_DIR="JellyfinGraveyardAnalytics/bin/Release/net9.0/publish"
DEST_DIR="Releases/$VERSION"
ZIP_NAME="JellyfinGraveyardAnalytics.zip"

mkdir -p "$DEST_DIR"

echo "📦 Preparing release $VERSION..."

FILES=("Dapper.dll" "JellyfinAnalyticsPlugin.dll")

for FILE in "${FILES[@]}"; do
  if [ -f "$SOURCE_DIR/$FILE" ]; then
    cp "$SOURCE_DIR/$FILE" "$DEST_DIR/"
    echo "✅ Copied $FILE to $DEST_DIR"
  else
    echo "❌ ERROR: $FILE not found in $SOURCE_DIR. Did you run 'dotnet publish -c Release'?"
    exit 1
  fi
done

echo "🗜️  Zipping assets..."
cd "$DEST_DIR" || exit
zip -q "$ZIP_NAME" "${FILES[@]}"

echo "🔐 Calculating MD5 Checksum..."
CHECKSUM=$(md5 -q "$ZIP_NAME")

echo "---"
echo "🎉 Release $VERSION is ready!"
echo "📍 Location: $DEST_DIR/$ZIP_NAME"
echo "📝 Checksum for manifest.json: $CHECKSUM"
echo "---"
