# Supply Chain Risk Summary

Date: 2026-05-13

## Short Version

This repo does use GitHub Actions. I did not find evidence that it automatically updates committed project dependencies through Dependabot, Renovate, npm, pnpm, yarn, or similar dependency automation.

The main supply-chain concern is that deploy/bootstrap automation installs tools from the network at runtime, including a `curl | sh` SpacetimeDB installer. That means a deploy or fresh server bootstrap can execute code fetched from outside the repo without a committed version, checksum, or review step in this project.

## What I Found

- GitHub Actions workflows exist in `.github/workflows/`.
- `infrastructure.yml` validates Terraform on relevant pull requests and manual dispatch.
- `spacetimedb-deploy.yml` is a manual deploy workflow that builds and publishes the SpacetimeDB module.
- Unity dependencies are declared in `Packages/manifest.json` and locked in `Packages/packages-lock.json`.
- Rust dependencies are declared in `server/Cargo.toml` and locked in `server/Cargo.lock`.
- Terraform providers are locked in `infrastructure/hetzner-spacetimedb/.terraform.lock.hcl`.
- No Dependabot/Renovate config was found.

## Higher-Risk Spots

- `.github/workflows/spacetimedb-deploy.yml` installs Rust `stable`, which can change over time.
- `.github/workflows/spacetimedb-deploy.yml` installs `binaryen` through `apt-get`.
- `.github/workflows/spacetimedb-deploy.yml` runs `curl -sSf https://install.spacetimedb.com | sh -s -- --yes`.
- `infrastructure/hetzner-spacetimedb/cloud-init.yaml.tftpl` also runs the SpacetimeDB installer with `curl | sh` during server bootstrap.
- `Packages/manifest.json` references the SpacetimeDB Unity SDK by GitHub tag. The Unity lockfile records a commit hash, but the manifest itself still points at a tag.
- GitHub Actions are referenced by version tags such as `actions/checkout@v4` and `hashicorp/setup-terraform@v3`, not immutable commit SHAs.

## Defensive Proposals

1. Replace `curl | sh` installs with a pinned SpacetimeDB CLI version plus checksum verification.

2. Add a `rust-toolchain.toml` file and pin the Rust toolchain used for CI/deploy builds instead of floating on latest `stable`.

3. Pin GitHub Actions to commit SHAs rather than mutable major-version tags.

4. Pin the Unity SpacetimeDB SDK dependency to an immutable commit SHA in `Packages/manifest.json`, or document a controlled process for updating the tag and lockfile together.

5. Add CI checks that fail if dependency lockfiles change unexpectedly during validation or deploy builds.

6. Use locked Cargo resolution during CI/deploy where supported, and verify `server/Cargo.lock` remains unchanged after builds.

7. Add an advisory scan step for Rust dependencies, such as `cargo audit` or an equivalent GitHub security workflow.

8. Keep deploy workflows manually triggered unless/until the runtime installs are pinned and verified.

## Recommended Priority

Start with the SpacetimeDB installer. It is the clearest issue because it downloads and executes remote code during deploy/server bootstrap. After that, pin Rust and GitHub Actions, then add dependency scanning and lockfile drift checks.
