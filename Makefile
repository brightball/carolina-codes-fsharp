DOTNET ?= dotnet
export DOTNET_CLI_TELEMETRY_OPTOUT := 1

ifneq ($(wildcard $(HOME)/.local/share/mise/dotnet-root/dotnet),)
  export DOTNET_ROOT ?= $(HOME)/.local/share/mise/dotnet-root
  export PATH := $(DOTNET_ROOT):$(PATH)
endif

ifneq ($(wildcard $(HOME)/.local/share/mise/installs/gitleaks/latest/gitleaks),)
  export PATH := $(HOME)/.local/share/mise/installs/gitleaks/latest:$(PATH)
endif

# Gitea prep vendors NuGet, semgrep, and gitleaks under .ci/. Check jobs
# restore that tree; restore is then a no-op so packages are not fetched again.
ifneq ($(wildcard $(CURDIR)/.ci/nuget/*),)
  export NUGET_PACKAGES ?= $(CURDIR)/.ci/nuget
  export DOTNET_CLI_HOME ?= $(CURDIR)/.ci/dotnet
endif

ifneq ($(wildcard $(CURDIR)/.ci/semgrep),)
  export PYTHONPATH := $(CURDIR)/.ci/semgrep$(if $(PYTHONPATH),:$(PYTHONPATH),)
  export PATH := $(CURDIR)/.ci/semgrep/bin:$(PATH)
endif

ifneq ($(wildcard $(CURDIR)/.ci/bin),)
  export PATH := $(CURDIR)/.ci/bin:$(PATH)
endif

.PHONY: test sast audit lint secrets check hooks restore

restore:
ifeq ($(wildcard $(CURDIR)/.ci/nuget/*),)
	$(DOTNET) restore Carolina.sln --nologo
	$(DOTNET) tool restore
else
	@echo "using vendored .ci/nuget"
endif

test: restore
	$(DOTNET) test Carolina.sln --nologo --no-restore

# Semgrep CE generic rules over this repo's F# (not C#-only Roslyn analyzers).
sast:
	semgrep --config $(CURDIR)/semgrep.yml --error --metrics=off --exclude .ci $(CURDIR)

audit: restore
	@out="$$($(DOTNET) list Carolina.sln package --vulnerable --include-transitive 2>&1)" || { echo "$$out"; exit 1; }; \
	echo "$$out"; \
	if echo "$$out" | grep -q "has the following vulnerable packages"; then exit 1; fi; \
	echo "$$out" | grep -q "has no vulnerable packages" || { echo "nuget audit produced no vulnerability report" >&2; exit 1; }

lint: restore
	$(DOTNET) fantomas --check .

secrets:
	gitleaks detect --no-git --source $(CURDIR) --verbose --redact --no-banner

check: test sast audit secrets lint

hooks:
	pre-commit install
	git config core.hooksPath .githooks
