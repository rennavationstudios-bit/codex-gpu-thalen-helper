# Code signing

The initial beta is intentionally unsigned because no Authenticode certificate is available. The installer and release notes must say so.

A future signed release should use an organization-validated or extended-validation code-signing certificate held by an approved hardware/cloud signing service. The private key and certificate password must never enter the repository, build logs, pull-request jobs, or ordinary artifacts.

Sign every shipped PE executable and the final installer with SHA-256 and a trusted timestamp, then verify signatures on a clean Windows machine. Keep GitHub checksums, SBOM, and attestations as complementary controls; they are not Authenticode substitutes.
