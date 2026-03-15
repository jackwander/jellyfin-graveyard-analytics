#!/bin/bash

# Check if a version argument was provided
if [ -z "$1" ]; then
  echo "Usage: ./release.sh vX.X.X.X"
  exit 1
fi

VERSION=$1
SOURCE_DIR="JellyfinGraveyardAnalytics/bin/Release/net9.0/publish"
DEST_DIR="Releases/$VERSION"

# Create the dynamic release folder
mkdir -p "$DEST_DIR"

echo "📦 Preparing release $VERSION..."

# List of files to move
FILES=("Dapper.dll" "JellyfinAnalyticsPlugin.dll")

for FILE in "${FILES[@]}"; do
  if [ -f "$SOURCE_DIR/$FILE" ]; then
    cp "$SOURCE_DIR/$FILE" "$DEST_DIR/"
    echo "✅ Copied $FILE to $DEST_DIR"
  else
    echo "❌ ERROR: $FILE not found in $SOURCE_DIR"
    exit 1
  fi
done

echo "---"
echo "🎉 Release $VERSION is ready in the /$DEST_DIR folder."
