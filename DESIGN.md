# Paparr interface design

Paparr uses Astryx with the shipped Matcha theme. The queue, import review, library integrity, worker status and owner sign-in share the same component set. The root theme follows the device color scheme and serves its fonts locally.

Use Astryx components for navigation, interactive controls, feedback, and status. Use its semantic CSS tokens for custom layout and data displays. Matcha supplies the olive and sage surfaces, DM Sans interface text, and Playwrite US Trad display face. Keep operational labels and long metadata easy to scan. Reserve the Paparr book mark for the brand link, sign-in, and favicon.

Place intake decisions in reading order: source file, metadata evidence, proposed destination, then approval. Keep filenames and paths selectable and allow them to wrap. Describe errors in text and keep recovery actions near the affected record. On narrow screens provide access to every queue and action without page-level horizontal scrolling.

Find the implementation plan and verification record in [the overhaul notes](docs/astryx-overhaul.md). See the [official Astryx documentation](https://github.com/meta/astryx) for component and theming guidance.
