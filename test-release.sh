#!/bin/bash

# Check if a version argument was provided
if [ -z "$1" ]; then
  echo "Usage: ./test-release.sh 1.2.0.0"
  exit 1
fi

VERSION=$1
SOURCE_DIR="JellyfinGraveyardAnalytics/bin/Release/net9.0/publish"

# Define the exact remote path using normal spaces
REMOTE_PATH="/home/jackwander/docker/jellyfin/config/data/plugins/Jellyfin Graveyard Analytics_$VERSION"

echo "📦 Building version $VERSION for testing..."
cd "JellyfinGraveyardAnalytics" && dotnet publish -c Release && cd ../

FILES=("Dapper.dll" "JellyfinGraveyardAnalyticsPlugin.dll")

# Verify files exist before attempting to sync
for FILE in "${FILES[@]}"; do
  if [ ! -f "$SOURCE_DIR/$FILE" ]; then
    echo "❌ ERROR: $FILE not found in $SOURCE_DIR. Build may have failed."
    exit 1
  fi
done

echo "🚀 Syncing files to remote server..."

# 1. Create the remote directory (single quotes protect the space on the remote shell)
ssh jackwander@10.10.1.201 "mkdir -p '$REMOTE_PATH'"

# 2. Rsync the files.
# We put single quotes around the path so rsync passes them to the remote SSH server safely!
rsync -avz "$SOURCE_DIR/Dapper.dll" "$SOURCE_DIR/JellyfinGraveyardAnalyticsPlugin.dll" "jackwander@10.10.1.201:'$REMOTE_PATH/'"

# 3. Restart the Jellyfin Docker container
echo "🔄 Restarting Jellyfin Docker container..."
ssh jackwander@10.10.1.201 "docker restart jellyfin"

echo "---"
echo "🎉 Test deployment of $VERSION is complete!"
echo "📍 Synced to: $REMOTE_PATH"
echo "---"
