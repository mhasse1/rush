#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING_DIR="${RUSH_STAGING:-$SCRIPT_DIR/dist}"
BUILD_SHA_FILE="$SCRIPT_DIR/.last-build-sha"
PUBLISH_TMP="$SCRIPT_DIR/.publish-tmp"
REPO="mhasse1/rush"

export PATH="/opt/homebrew/opt/dotnet/bin:/opt/homebrew/Cellar/dotnet/10.0.105/libexec:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

CURRENT_SHA=$(git -C "$SCRIPT_DIR" rev-parse HEAD 2>/dev/null || echo "unknown")

# ── --full: CI-based install + distribute ─────────────────────────────
if [[ "${1:-}" == "--full" ]]; then
    mkdir -p "$STAGING_DIR"

    echo "Rush install (CI mode)"
    echo "  Commit: ${CURRENT_SHA:0:7}"
    echo ""

    # Find the CI run for the current commit
    RUN_ID=$(gh run list -R "$REPO" --branch main --json databaseId,headSha \
        -q ".[] | select(.headSha == \"$CURRENT_SHA\") | .databaseId" 2>/dev/null | head -1)

    if [[ -z "$RUN_ID" ]]; then
        echo "  ! No CI run found for ${CURRENT_SHA:0:7}. Push first."
        exit 1
    fi

    echo "  CI run: $RUN_ID"

    # Stream deploys as each platform finishes
    PLATFORMS=(
        "osx-arm64:rush-osx-arm64:rush:rush-osx-arm64"
        "linux-x64:rush-linux-x64:rush:rush-linux-x64"
        "linux-arm64:rush-linux-arm64:rush:rush-linux-arm64"
        "win-x64:rush-win-x64:rush.exe:rush_x64.exe"
        "win-arm64:rush-win-arm64:rush.exe:rush_arm64.exe"
    )

    DEPLOYED=0
    WAITED=0
    MAX_WAIT=600  # 10 minutes max

    while [[ $DEPLOYED -lt ${#PLATFORMS[@]} && $WAITED -lt $MAX_WAIT ]]; do
        for i in "${!PLATFORMS[@]}"; do
            IFS=: read -r rid artifact src_name dst_name <<< "${PLATFORMS[$i]}"
            [[ "$rid" == "done" ]] && continue

            # Check if this job is done
            job_status=$(gh run view "$RUN_ID" -R "$REPO" --json jobs \
                --jq ".jobs[] | select(.name | contains(\"$rid\")) | .conclusion" 2>/dev/null)

            if [[ "$job_status" == "success" ]]; then
                echo "  ✓ $rid: CI passed — downloading..."
                local_tmp=$(mktemp -d)
                if gh run download "$RUN_ID" -R "$REPO" -n "$artifact" -D "$local_tmp" 2>/dev/null; then
                    # Stage the binary
                    mv "$local_tmp/$src_name" "$STAGING_DIR/$dst_name"
                    [[ "$src_name" == "rush" ]] && chmod +x "$STAGING_DIR/$dst_name"
                    # Strip macOS quarantine + ad-hoc sign (CI downloads get SIGKILL without this)
                    if [[ "$(uname)" == "Darwin" ]]; then
                        xattr -cr "$STAGING_DIR/$dst_name" 2>/dev/null || true
                        codesign --sign - --force "$STAGING_DIR/$dst_name" 2>/dev/null || true
                    fi
                    echo "    → $STAGING_DIR/$dst_name"

                    # Install locally if this is our platform
                    if [[ "$rid" == "osx-arm64" && "$(uname -m)" == "arm64" && "$(uname)" == "Darwin" ]]; then
                        sudo cp "$STAGING_DIR/$dst_name" "$BIN_LINK"
                        # Re-sign after copy (cp strips code signature)
                        sudo xattr -cr "$BIN_LINK" 2>/dev/null || true
                        sudo codesign --sign - --force "$BIN_LINK" 2>/dev/null || true
                        VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
                        echo "    → $BIN_LINK ($VERSION)"
                        echo "$CURRENT_SHA" > "$BUILD_SHA_FILE"
                    fi
                else
                    echo "    ! Failed to download $artifact"
                fi
                rm -rf "$local_tmp"
                PLATFORMS[$i]="done:::::"
                DEPLOYED=$((DEPLOYED + 1))

            elif [[ "$job_status" == "failure" ]]; then
                echo "  ✗ $rid: CI FAILED"
                PLATFORMS[$i]="done:::::"
                DEPLOYED=$((DEPLOYED + 1))
            fi
        done

        # Check if all done
        all_done=true
        for p in "${PLATFORMS[@]}"; do
            [[ "$p" != "done:::::" ]] && all_done=false && break
        done
        $all_done && break

        sleep 10
        WAITED=$((WAITED + 10))

        # Progress indicator
        if [[ $((WAITED % 30)) -eq 0 ]]; then
            remaining=0
            for p in "${PLATFORMS[@]}"; do
                [[ "$p" != "done:::::" ]] && remaining=$((remaining + 1))
            done
            echo "  ... waiting for $remaining platform(s) ($((WAITED))s)"
        fi
    done

    if [[ $WAITED -ge $MAX_WAIT ]]; then
        echo "  ! Timed out waiting for CI"
    fi

    # Register as login shell
    if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
        echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
    fi

    # Docs
    rm -f "$STAGING_DIR/rush-lang-spec.yaml" "$STAGING_DIR/user-manual.md"
    ln -f "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$STAGING_DIR/rush-lang-spec.yaml" 2>/dev/null || true
    ln -f "$SCRIPT_DIR/docs/user-manual.md" "$STAGING_DIR/user-manual.md" 2>/dev/null || true

    echo ""
    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "not installed locally")
    echo "Local: $VERSION"
    echo "Staged: $STAGING_DIR/"
    ls -lh "$STAGING_DIR/" 2>/dev/null
    exit 0
fi

# ── Default: local build + install ────────────────────────────────────

LAST_SHA=""
[[ -f "$BUILD_SHA_FILE" ]] && LAST_SHA=$(cat "$BUILD_SHA_FILE")

FORCE="${FORCE:-false}"
if [[ "$CURRENT_SHA" == "$LAST_SHA" && "$FORCE" != "true" && -f "$BIN_LINK" ]]; then
    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
    echo "Already built: $VERSION (commit ${CURRENT_SHA:0:7})"
    echo "Use FORCE=true ./install.sh to rebuild, or ./install.sh --full for CI."
else
    rm -rf "$PUBLISH_TMP" "$PUBLISH_TMP"-*
    echo "Building release binary..."
    dotnet publish -c Release -r osx-arm64 -o "$PUBLISH_TMP" "$SCRIPT_DIR"
    echo ""

    sudo rm -rf /usr/local/lib/rush
    sudo mkdir -p /usr/local/lib/rush
    sudo cp -rf "$PUBLISH_TMP"/* /usr/local/lib/rush/
    sudo rm -f "$BIN_LINK"
    sudo ln -sf /usr/local/lib/rush/rush "$BIN_LINK"

    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
    echo "$CURRENT_SHA" > "$BUILD_SHA_FILE"
fi

# Register as login shell
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

echo ""
echo "Installed: $VERSION"
