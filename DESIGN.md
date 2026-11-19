---
name: Paparr Accession Ledger
description: A walnut and brass accession register for a personal ebook library.
colors:
  primary: "#d8ac70"
  brass-hover: "#efc78f"
  desk: "#1b1410"
  walnut: "#2c2019"
  spine: "#433025"
  paper: "#ecdcc2"
  muted: "#c2aa90"
  line: "#654b3b"
  review-background: "#512f19"
  review-text: "#f0c38a"
  imported-background: "#2b4034"
  imported-text: "#c3dfc5"
  attention-background: "#492820"
  attention-text: "#ffcec0"
  working-background: "#2b3645"
  working-text: "#cadcf3"
typography:
  page-title:
    fontFamily: "Libre Baskerville, Georgia, serif"
    fontSize: "54px"
    fontWeight: 400
    lineHeight: 1.08
    letterSpacing: "-1px"
  record-title:
    fontFamily: "Libre Baskerville, Georgia, serif"
    fontSize: "36px"
    fontWeight: 400
    lineHeight: 1.2
  book-title:
    fontFamily: "Georgia, serif"
    fontSize: "18px"
    fontWeight: 400
    lineHeight: 1.2
  count:
    fontFamily: "Georgia, serif"
    fontSize: "26px"
    fontWeight: 400
  metadata:
    fontFamily: "DM Sans, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.6
  control:
    fontFamily: "DM Sans, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 600
  section-title:
    fontFamily: "DM Sans, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 700
  destination-path:
    fontFamily: monospace
    fontSize: "12px"
    lineHeight: 1.8
rounded:
  control: "4px"
  navigation: "3px"
spacing:
  control-gap: "10px"
  compact-inset: "12px"
  inset: "16px"
  section-gap: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.desk}"
    typography: "{typography.control}"
    rounded: "{rounded.control}"
    padding: "10px 17px"
  button-primary-hover:
    backgroundColor: "{colors.brass-hover}"
  button-approval:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.desk}"
    typography: "{typography.control}"
    rounded: "{rounded.control}"
    padding: "13px"
    width: "100%"
  button-secondary:
    backgroundColor: "{colors.walnut}"
    textColor: "{colors.paper}"
    typography: "{typography.control}"
    rounded: "{rounded.control}"
    padding: "10px 17px"
  button-secondary-hover:
    backgroundColor: "{colors.spine}"
  search-field:
    backgroundColor: "{colors.walnut}"
    textColor: "{colors.paper}"
    rounded: "{rounded.control}"
    padding: "9px 14px"
  status-tag:
    backgroundColor: "{colors.spine}"
    textColor: "{colors.paper}"
    rounded: "{rounded.control}"
    padding: "4px 7px"
  status-tag-review:
    backgroundColor: "{colors.review-background}"
    textColor: "{colors.review-text}"
  status-tag-imported:
    backgroundColor: "{colors.imported-background}"
    textColor: "{colors.imported-text}"
  status-tag-attention:
    backgroundColor: "{colors.attention-background}"
    textColor: "{colors.attention-text}"
  status-tag-working:
    backgroundColor: "{colors.working-background}"
    textColor: "{colors.working-text}"
  navigation-current:
    backgroundColor: "{colors.spine}"
    textColor: "{colors.primary}"
    rounded: "{rounded.navigation}"
    padding: "13px 12px"
  book-row-selected:
    backgroundColor: "{colors.spine}"
    textColor: "{colors.paper}"
    padding: "22px 18px"
  candidate-selected:
    backgroundColor: "{colors.spine}"
    textColor: "{colors.paper}"
    rounded: "{rounded.control}"
    padding: "14px"
  folder-profile:
    backgroundColor: "{colors.desk}"
    textColor: "{colors.paper}"
    rounded: "{rounded.control}"
    padding: "{spacing.compact-inset}"
  destination-preview:
    backgroundColor: "{colors.desk}"
    textColor: "{colors.paper}"
    rounded: "{rounded.control}"
    padding: "17px"
---

# Design System for Paparr

## Overview

**Creative North Star: "Accession Ledger"**

Paparr’s imports and review desk is the first implementation of Accession Ledger. Walnut and espresso surfaces recall a library desk. Parchment type and thin rules make filenames, metadata evidence, and destinations easy to compare. Serif book titles and clothbound initials bring in the library reference while keeping the register compact.

The imports sidebar shares this treatment while the review desk is open. The sign-in, library health, and worker status views retain their existing styling. The review desk stylesheet and shared base styles are the implementation sources. `@src/Paparr.UI/src/accession-ledger.css` and `@src/Paparr.UI/src/index.css`

**Key Characteristics**

- Espresso and walnut planes divided by thin rules
- Parchment text, serif titles, and compact sans-serif controls
- Brass selection markers and approval controls
- Small clothbound book initials in the queue

## Colors

### Primary

Brass marks selected records, checked metadata, the current filter, and keyboard focus. The approval button pairs a solid brass fill with espresso text. Its hover state uses a lighter brass.

### Secondary

Status tags pair a subdued fill with readable text. Amber signals review, green signals a completed import, clay signals attention, and slate blue signals work in progress. Keep the status words visible alongside the color.

### Neutral

Espresso grounds the desk, queue, and inset evidence. Walnut defines the sidebar and detail pane. The lighter spine tone marks a hovered or selected row and the selected metadata card. Parchment carries titles and essential text. Muted parchment supports filenames and evidence. Warm rules divide rows, panes, and counts.

**The Brass Rule.** Use solid brass for the main action and narrow brass details for selection and focus.

## Typography

Page and record headings use the Libre Baskerville stack with Georgia as the fallback. Queue titles and counts explicitly use Georgia. Metadata and controls use the DM Sans stack with a system sans fallback. The app currently declares the named web fonts without bundling them.

The page title scales from 34 to 54px with a preferred size of 4vw. The record title scales from 26 to 36px with a preferred size of 3vw. The frontmatter records their upper sizes. Queue titles stay at 18px. Counts use tabular numerals so changing totals keep the same alignment. Destination paths set in monospace wrap when space runs short.

## Layout

On wide screens, a 240px sticky sidebar frames a split workspace with a narrower queue and a wider record. The status strip uses three columns separated by rules. At 1100px and below, the sidebar reduces to 205px and the pane padding tightens. At 800px and below, sidebar navigation scrolls horizontally above the content. The queue becomes a horizontal row above the detail pane, and each status count sits above its label.

The record begins with the title, import state, and filename. Metadata options lead to the destination preview and approval. Duplicates and history follow when present.

## Elevation & Depth

Tonal planes and one-pixel borders separate workspace regions. The book mark and queue thumbnails use a narrow inset spine, and thumbnails add a small shadow to establish their book silhouette. Feedback comes from immediate changes to fill, border, and outline.

## Shapes

Controls, chips, and inset panels use a small radius of 4px. Navigation uses 3px corners. Book thumbnails and the brand mark round the outer edge slightly more than the spine edge. The selected queue row uses a 3px-wide brass marker set in from the top and bottom.

## Components

### Buttons and inputs

Approval is a full-width primary button beneath its destination preview. Secondary actions use walnut and a warm border, shifting to the spine tone on hover. Search puts the icon and input inside one bordered control, with a brass outline around the entire field when focused. Keyboard focus uses a 2px brass outline.

### Navigation and status

The current filter uses a spine fill and bold brass text. Small counts align at the row end. Status chips show the state in words inside the queue and the selected record.

### Queue rows and metadata choices

A queue row presents the title, author, filename, and status beside a clothbound initial. Selecting a row adds the brass spine marker and updates the adjacent record. Metadata choices keep a visible radio control, source, and evidence. The chosen card uses a spine-toned fill and a brass border.

### Record insets

Folder profile, destination preview, and notices sit on inset espresso. The profile uses a two-column definition list. The destination path stays selectable and wraps across narrow screens. Attention notices use the same clay pairing as the failure tag.

## Do's and Don'ts

- Do give book titles the serif hierarchy and use sans-serif for metadata and controls.
- Do keep status words and a visible keyboard outline alongside color cues.
- Do set destination paths beside the action that publishes to them.
- Don't add raised shadows to panels or navigation.
- Don't use the brass fill for routine secondary controls.
