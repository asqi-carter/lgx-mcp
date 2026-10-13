.PHONY: build build-nosdk test lint-ps integration publish-win publish-linux publish-win-nosdk publish-linux-nosdk release-nosdk cold-gate cold-gate-win

build:
	dotnet build LogixMcp.sln --nologo

# No-SDK build gate: builds the solution with -p:EnableLogixSdk=false
# in a throwaway copy of the WORKING TREE (so uncommitted edits are checked),
# the way a clean clone without a licensed Studio 5000 install would see it.
# The copy leaves out .git, .localfeed contents, bin/ and obj/ (an empty
# .localfeed/ is recreated empty because the SDK-lane NuGet.config lists it as a
# restore source; the no-SDK NuGet.config lists only nuget.org), so the
# real tree's bin/ and obj/ are never touched. Any build error fails the make
# run; the temp dir is removed on success, failure and interrupt.
build-nosdk:
	@tmp=$$(mktemp -d "$${TMPDIR:-/tmp}/logix-nosdk.XXXXXX") || exit 1; \
	trap 'rm -rf "$$tmp"' EXIT INT TERM; \
	echo "build-nosdk: copying working tree to $$tmp"; \
	tar --exclude=./.git --exclude=./.localfeed --exclude=bin --exclude=obj -cf - . | tar -xf - -C "$$tmp" || exit 1; \
	mkdir -p "$$tmp/.localfeed"; \
	cd "$$tmp" && dotnet build LogixMcp.sln --nologo -p:EnableLogixSdk=false || { echo 'FAIL: build-nosdk (no-SDK build has errors)'; exit 1; }; \
	echo 'build-nosdk OK'

# Parse-check every bootstrap/bench PowerShell script using the built-in PS AST
# parser. Fails immediately on the first file that contains a syntax error and
# prints the parser diagnostics.  Fails with a non-zero exit when pwsh
# (PowerShell Core) is not in PATH — a lint gate that degrades to a skip is
# not a gate.
#
# The ASCII gate runs first (pure Linux, no pwsh required): it scans every file
# matching bootstrap/*.ps1 bench/*.ps1 for bytes outside the 0x00-0x7F range
# (a portable python3 check; grep -P is not available on macOS, so it is not used)
# and prints the filename and line number of every hit before exiting
# non-zero.  This catches the encoding class that pwsh 7 silently accepts but
# PS 5.1 rejects, closing the structural blind spot that previously let
# lint-ps report OK on unrunnable scripts.
lint-ps:
	@printf 'ASCII gate: scanning bootstrap/*.ps1 bench/*.ps1 for non-ASCII bytes\n'; \
	command -v python3 >/dev/null 2>&1 || { echo 'FAIL: ASCII gate needs python3 (not found in PATH)'; exit 1; }; \
	python3 -c 'import sys; bad=[f"{f}:{n}" for f in sys.argv[1:] for n,l in enumerate(open(f,"rb").read().split(b"\n"),1) if any(c>127 for c in l)]; print("\n".join(bad)); sys.exit(1 if bad or not sys.argv[1:] else 0)' bootstrap/*.ps1 bench/*.ps1 > .ascii-gate.out; rc=$$?; \
	if [ $$rc -ne 0 ]; then \
		cat .ascii-gate.out; rm -f .ascii-gate.out; \
		echo 'FAIL: non-ASCII bytes detected in bootstrap/*.ps1 bench/*.ps1 (see above)'; \
		exit 1; \
	fi; \
	rm -f .ascii-gate.out; \
	echo 'ASCII gate OK'
	@if command -v pwsh >/dev/null 2>&1; then \
		failed=0; \
		for f in bootstrap/*.ps1 bench/*.ps1; do \
			[ -e "$$f" ] || continue; \
			printf '  checking %s ...\n' "$$f"; \
			DOTNET_ROLL_FORWARD=LatestMajor pwsh -NoProfile -Command '$$errs=@(); [void][System.Management.Automation.Language.Parser]::ParseFile("'"$$f"'",[ref]$$null,[ref]$$errs); if($$errs.Count -gt 0){$$errs|ForEach-Object{Write-Host("  "+$$_.Message)};exit 1}' \
			|| { echo "FAIL: parse error in $$f"; failed=1; }; \
		done; \
		[ "$$failed" -eq 0 ] && echo 'lint-ps OK' || exit 1; \
	else \
		echo 'ERROR: lint-ps requires pwsh (PowerShell Core) but it was not found in PATH'; \
		exit 1; \
	fi

# Pipeline test gate. Tolerant of the pre-scaffold state (before the solution
# exists); after that, a missing solution is a real failure.
test: lint-ps build-nosdk
	@if ls *.sln >/dev/null 2>&1; then dotnet test --nologo; else echo "no solution yet - skipping"; fi

# Run only tests tagged Category=Integration (requires AB_SERVER_HOST to point at a live simulator).
integration:
	dotnet test --nologo --filter Category=Integration

# Produce a self-contained single-file Windows x64 executable from the core
# LogixMcp project (no .NET runtime required on the target machine).
#
# Immediately after `dotnet publish`, bootstrap/*.ps1 (and bootstrap/README*
# if present) are copied into <publish>/bootstrap/, before any guard runs —
# these are the scripts a cold-install operator needs to provision and gate
# the install, and the artifact is incomplete without them.
#
# bench/*.ps1 (e.g. bench/studio-edits.ps1, the script Session1ScriptRunner
# looks for beside the running binary) is copied into
# <publish>/bench/ the same way, immediately afterward and also before any
# guard runs.
#
# scripts/verify-vendor-closure.sh then runs three post-publish guards:
#
#   1. Bootstrap-completeness guard (scripts/verify-bootstrap.sh) — asserts
#      that setup.ps1, cold-install-gate.ps1, and provision-sdk.ps1 are all
#      present by name in <publish>/bootstrap/, and that bench/studio-edits.ps1
#      is present in <publish>/bench/, failing the build and naming every
#      absentee if the copy steps above did not land all of them.
#
#   2. RockwellAutomation absence guard — recursively scans the entire publish
#      output tree (including sdk/vendor/) and tools/ (e.g. tools/echoctl) for
#      any file whose name starts with "RockwellAutomation" and fails the
#      build if one is found.  These DLLs are non-redistributable and must
#      come from the user's licensed Studio 5000 / FactoryTalk Logix Echo
#      installation, never from the shipped artifact or the repo itself.
#
#   3. Vendor-closure completeness guard — reads the SdkVendorHarvest .deps.json
#      produced during the publish step to enumerate every assembly in the
#      transitive runtime closure of the Rockwell SDK (excluding
#      RockwellAutomation* non-redistributable DLLs), then asserts that each
#      one is present by name in sdk/vendor/ of the publish output.  A
#      count-only ("non-empty") check is not used; the guard fails the build
#      and names every absent assembly, preventing a partial closure (e.g.
#      4-of-15) from shipping as green.
#
# Restore for win-x64 before publishing: a preceding Linux CI build (e.g.
# `make build`, `make test`) populates obj/project.assets.json without the
# win-x64 RID, so `dotnet publish -r win-x64` fails with NETSDK1047 ("Assets
# file ... doesn't have a target for RuntimeIdentifier 'win-x64'") unless a
# win-x64 restore runs first. The restore below carries the same RID and
# MSBuild properties as the publish that follows it — as properties, because
# `dotnet restore` rejects the publish-only switches `--self-contained` and
# `-c` (MSB1001 "Unknown switch", found on the first post-merge publish
# 2026-09-06 22:45Z).
publish-win:
	dotnet restore src/LogixMcp/LogixMcp.csproj \
		-r win-x64 \
		-p:SelfContained=true \
		-p:PublishSingleFile=true \
		-p:EnableLogixSdk=true \
		-p:Configuration=Release
	dotnet publish src/LogixMcp/LogixMcp.csproj \
		-r win-x64 \
		--self-contained \
		-p:PublishSingleFile=true \
		-p:EnableLogixSdk=true \
		-c Release \
		--nologo
	@mkdir -p src/LogixMcp/bin/Release/net10.0/win-x64/publish/bootstrap
	@cp bootstrap/*.ps1 src/LogixMcp/bin/Release/net10.0/win-x64/publish/bootstrap/
	@if ls bootstrap/README* >/dev/null 2>&1; then \
		cp bootstrap/README* src/LogixMcp/bin/Release/net10.0/win-x64/publish/bootstrap/; \
	fi
	@mkdir -p src/LogixMcp/bin/Release/net10.0/win-x64/publish/bench
	@cp bench/*.ps1 src/LogixMcp/bin/Release/net10.0/win-x64/publish/bench/
	@bash scripts/verify-vendor-closure.sh \
		src/LogixMcp/bin/Release/net10.0/win-x64/publish \
		src/LogixMcp.SdkVendorHarvest/bin/Release/net10.0/vendor-publish/LogixMcp.SdkVendorHarvest.deps.json
	@echo "publish-win OK: no RockwellAutomation binaries in publish output; sdk/vendor/ closure verified; bootstrap/ and bench/ scripts present"

# Produce a self-contained single-file Linux x86-64 executable from the core
# LogixMcp project (no .NET runtime required on the target machine).
# Used by the cold-install release gate in Linux CI.
#
# Same RockwellAutomation guard as publish-win, extended to also scan tools/
# (e.g. tools/echoctl) so a Rockwell DLL accidentally committed there fails
# the build exactly like one leaking into the publish output would.
#
# Immediately after `dotnet publish`, bootstrap/*.ps1 (and bootstrap/README*
# if present) are copied into <publish>/bootstrap/, and bench/*.ps1 is copied
# into <publish>/bench/, before either guard runs — the linux-x64 artifact
# ships the same cold-install scripts and bench scripts as win-x64.
# scripts/verify-bootstrap.sh (the sibling guard also used from within
# scripts/verify-vendor-closure.sh on the win-x64 lane) then asserts that
# setup.ps1, cold-install-gate.ps1, and provision-sdk.ps1 are all present by
# name in <publish>/bootstrap/, and that studio-edits.ps1 is present in
# <publish>/bench/, failing the build and naming every absentee otherwise.
publish-linux:
	dotnet publish src/LogixMcp/LogixMcp.csproj \
		-r linux-x64 \
		--self-contained \
		-p:PublishSingleFile=true \
		-c Release \
		--nologo
	@mkdir -p src/LogixMcp/bin/Release/net10.0/linux-x64/publish/bootstrap
	@cp bootstrap/*.ps1 src/LogixMcp/bin/Release/net10.0/linux-x64/publish/bootstrap/
	@if ls bootstrap/README* >/dev/null 2>&1; then \
		cp bootstrap/README* src/LogixMcp/bin/Release/net10.0/linux-x64/publish/bootstrap/; \
	fi
	@mkdir -p src/LogixMcp/bin/Release/net10.0/linux-x64/publish/bench
	@cp bench/*.ps1 src/LogixMcp/bin/Release/net10.0/linux-x64/publish/bench/
	@if find src/LogixMcp/bin/Release/net10.0/linux-x64/publish $(wildcard tools) -name "RockwellAutomation*" | grep -q .; then \
		echo "ERROR: RockwellAutomation binaries found in publish output or tools/ — aborting."; \
		exit 1; \
	fi
	@bash scripts/verify-bootstrap.sh src/LogixMcp/bin/Release/net10.0/linux-x64/publish
	@echo "publish-linux OK: no RockwellAutomation binaries in publish output or tools/; bootstrap/ and bench/ scripts present"

# ---------------------------------------------------------------------------
# No-SDK publish and release.
#
# Self-contained, single-file, -p:EnableLogixSdk=false. No Rockwell package is
# needed, so nuget.org is forced as the only restore source
# (-p:RestoreSources), so the gitignored .localfeed/ (a source only in the
# SDK-lane NuGet.config) need not exist. Output goes to a dedicated publish-nosdk/ directory (never
# the SDK publish/ dir) so a stale SDK publish cannot leak sdk/ into it.
# bootstrap/ is copied minus provision-sdk.ps1 (there is no SDK to provision);
# bench/ is not shipped in the no-SDK publish. scripts/verify-nosdk-publish.sh
# then asserts no RockwellAutomation* binary, no sdk/ directory, no
# provision-sdk.ps1, and the required scripts present.
# ---------------------------------------------------------------------------
NOSDK_FRAMEWORK := net10.0
NOSDK_PROPS := -p:EnableLogixSdk=false -p:PublishSingleFile=true -p:RestoreSources=https://api.nuget.org/v3/index.json
NOSDK_OUT_WIN := src/LogixMcp/bin/Release/$(NOSDK_FRAMEWORK)/win-x64/publish-nosdk
NOSDK_OUT_LINUX := src/LogixMcp/bin/Release/$(NOSDK_FRAMEWORK)/linux-x64/publish-nosdk

publish-win-nosdk:
	rm -rf $(NOSDK_OUT_WIN)
	dotnet publish src/LogixMcp/LogixMcp.csproj \
		-r win-x64 \
		--self-contained \
		$(NOSDK_PROPS) \
		-c Release \
		-o $(NOSDK_OUT_WIN) \
		--nologo
	@mkdir -p $(NOSDK_OUT_WIN)/bootstrap
	@for f in bootstrap/*; do \
		[ -f "$$f" ] || continue; \
		case "$$(basename "$$f")" in \
			provision-sdk.ps1) ;; \
			*.ps1|README*) cp "$$f" $(NOSDK_OUT_WIN)/bootstrap/ ;; \
		esac; \
	done
	@bash scripts/verify-nosdk-publish.sh $(NOSDK_OUT_WIN)
	@echo "publish-win-nosdk OK: $(NOSDK_OUT_WIN)"

publish-linux-nosdk:
	rm -rf $(NOSDK_OUT_LINUX)
	dotnet publish src/LogixMcp/LogixMcp.csproj \
		-r linux-x64 \
		--self-contained \
		$(NOSDK_PROPS) \
		-c Release \
		-o $(NOSDK_OUT_LINUX) \
		--nologo
	@mkdir -p $(NOSDK_OUT_LINUX)/bootstrap
	@for f in bootstrap/*; do \
		[ -f "$$f" ] || continue; \
		case "$$(basename "$$f")" in \
			provision-sdk.ps1) ;; \
			*.ps1|README*) cp "$$f" $(NOSDK_OUT_LINUX)/bootstrap/ ;; \
		esac; \
	done
	@bash scripts/verify-nosdk-publish.sh $(NOSDK_OUT_LINUX)
	@echo "publish-linux-nosdk OK: $(NOSDK_OUT_LINUX)"

# Build both no-SDK publishes and package them as
# dist/lgx-mcp-<version>-<rid>.zip plus dist/SHA256SUMS. <version> is the
# <Version> in Directory.Build.props. scripts/package-nosdk.py re-checks each
# finished zip and fails (deleting the zip) if it contains a RockwellAutomation*
# file or any sdk/ directory, or lacks LICENSE, NOTICE, README.md, SECURITY.md,
# CHANGELOG.md, docs/ or licenses/ at the zip root; the publish guards run first via the publish
# targets. dist/ is cleared first so SHA256SUMS lists exactly this release.
release-nosdk: publish-win-nosdk publish-linux-nosdk
	@version=$$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -n1); \
	[ -n "$$version" ] || { echo 'FAIL: release-nosdk could not read <Version> from Directory.Build.props'; exit 1; }; \
	rm -rf dist && mkdir -p dist; \
	notice=NOTICE; \
	license=LICENSE; \
	readme=README.md; \
	security=SECURITY.md; \
	changelog=CHANGELOG.md; \
	docsdir=docs; \
	[ -f licenses/MPL-2.0.txt ] || { echo 'FAIL: release-nosdk needs licenses/MPL-2.0.txt (third-party licence texts)'; exit 1; }; \
	[ -f "$$notice" ] || { echo 'FAIL: release-nosdk could not find NOTICE'; exit 1; }; \
	[ -f "$$license" ] || { echo 'FAIL: release-nosdk could not find LICENSE'; exit 1; }; \
	[ -f "$$readme" ] || { echo 'FAIL: release-nosdk could not find README.md'; exit 1; }; \
	[ -f "$$security" ] || { echo 'FAIL: release-nosdk could not find SECURITY.md'; exit 1; }; \
	[ -f "$$changelog" ] || { echo 'FAIL: release-nosdk could not find CHANGELOG.md'; exit 1; }; \
	[ -d "$$docsdir" ] || { echo 'FAIL: release-nosdk could not find docs/'; exit 1; }; \
	for out in $(NOSDK_OUT_WIN) $(NOSDK_OUT_LINUX); do \
		rm -rf "$$out/licenses" && cp -r licenses "$$out/licenses" && cp "$$notice" "$$out/NOTICE" && cp "$$license" "$$out/LICENSE" && cp "$$readme" "$$out/README.md" && cp "$$security" "$$out/SECURITY.md" && cp "$$changelog" "$$out/CHANGELOG.md" && rm -rf "$$out/docs" && cp -r "$$docsdir" "$$out/docs" || exit 1; \
	done; \
	python3 scripts/package-nosdk.py $(NOSDK_OUT_WIN) "dist/lgx-mcp-$$version-win-x64.zip" || exit 1; \
	python3 scripts/package-nosdk.py $(NOSDK_OUT_LINUX) "dist/lgx-mcp-$$version-linux-x64.zip" || exit 1; \
	if command -v sha256sum >/dev/null 2>&1; then sha=sha256sum; \
	elif command -v shasum >/dev/null 2>&1; then sha="shasum -a 256"; \
	else echo 'FAIL: release-nosdk needs sha256sum or shasum'; exit 1; fi; \
	cd dist && $$sha lgx-mcp-$$version-*.zip > SHA256SUMS && cat SHA256SUMS; \
	echo "release-nosdk OK: dist/lgx-mcp-$$version-{win-x64,linux-x64}.zip + SHA256SUMS"

# Cold-install release gate — verifies the published artifact starts standalone
# with no repo, no .localfeed, and no .NET SDK accessible.
#
# On Linux/CI: publishes a self-contained linux-x64 binary, copies ONLY that
# binary to a clean temporary directory, launches it, and asserts logix_ping
# returns a valid response within 25 seconds.
#
# See scripts/cold-install-gate.sh for the full probe logic and
# docs/RELEASE_CHECKLIST.md for when to run this gate.
cold-gate: publish-linux
	@bash scripts/cold-install-gate.sh \
		src/LogixMcp/bin/Release/net10.0/linux-x64/publish/LogixMcp

# Cold-install release gate for the Windows (win-x64) publish artifact.
#
# MUST BE RUN ON WINDOWS (Git Bash or WSL) — the win-x64 LogixMcp.exe is a
# Windows PE executable and cannot be launched on Linux/CI.
#
# What it verifies (in addition to the file-inspection guards already run by
# publish-win / verify-vendor-closure.sh):
#
#   Base probe:
#     Launches LogixMcp.exe from a clean temporary directory (no repo, no
#     .localfeed, no NuGet cache, no LOGIX_SDK_PATH) and asserts logix_ping
#     returns status="ok".  A binary that crashes on startup (e.g. due to a
#     FileNotFoundException for a bundled Microsoft.Extensions.* assembly) fails
#     this gate even when all file-inspection guards pass — exactly the class
#     of defect.
#
#   Scenario A — adapter present (SDK build):
#     Asserts logix_ping succeeds and logix_connect backend="sdk" returns a
#     valid structured response (ConnectResult, ConnectError, or
#     ConnectSdkNotAvailableError).  A dead process is never a passing outcome.
#
#   Scenario B — adapter deliberately deleted:
#     Deletes sdk/LogixMcp.SdkAdapter.dll from the gate workdir and starts a
#     fresh server.  Asserts the process is still alive after initialize and
#     that logix_connect backend="sdk" returns ConnectSdkNotAvailableError
#     (payload must contain "probedPaths").  This proves the degradation path
#     is real rather than assumed.
#
# Run this gate manually on a Windows machine before tagging any Windows release:
#
#   make cold-gate-win
#
# Or point the script directly at a pre-built exe (e.g. from another machine):
#
#   bash scripts/cold-install-gate.sh \
#       src/LogixMcp/bin/Release/net10.0/win-x64/publish/LogixMcp.exe
#
# See docs/RELEASE_CHECKLIST.md Gate 3 for the mandatory sign-off checklist.
cold-gate-win: publish-win
	@bash scripts/cold-install-gate.sh \
		src/LogixMcp/bin/Release/net10.0/win-x64/publish/LogixMcp.exe

