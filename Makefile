release:
	@# Resumable: run it again (or with VERSION=x.y.z) after a failure part
	@# way; DRY_RUN=1 prints what it would do.
	scripts/release.sh

test:
	@echo "Fetching tags..."
	git fetch --tags
	@echo "Bumping version with git-cliff..."
	$(eval NEW_VERSION := $(shell git cliff --bumped-version))
	@echo "New version will be: $(NEW_VERSION)"
	@echo "Generating changelog..."
	@git cliff --unreleased --tag $(NEW_VERSION) --strip all > /tmp/release_notes.md
	@cat /tmp/release_notes.md

.PHONY: release test
