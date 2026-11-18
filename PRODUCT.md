# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

The primary user is the owner of a private, self-hosted ebook library. Other trusted users may have separate logins in the future; public access is not intended.

## Product Purpose

Paparr is an ebook intake workbench. It helps the owner review EPUB and PDF imports, choose metadata, and place files in an organized personal library.

## Operating Context

The owner drops files into a watch folder, reviews metadata candidates and destination previews, and approves imports. Paparr maintains import history and provides recovery, integrity checks, and an explicit Calibre export handoff.

## Capabilities and Constraints

- Every import currently requires review. Match scores describe title and author similarity, not probabilities.
- Publication verifies file content, preserves originals by default, and supports recovery after interruption.
- Paparr manages its own library directory and does not write to a Calibre database.
- The current interface was AI-generated; a styling overhaul is desired.

## Brand Commitments

The product is named Paparr. Its visual direction is dark academia, with deep browns and library references. Keep the workspace clean and allow a few playful details.

## Evidence on Hand

The README documents current capabilities and includes a screenshot at `docs/images/review-desktop.png`.

## Product Principles

- Keep imports recoverable and protect existing library files.
- Make metadata choices and import history understandable.
- Keep the owner in control of publication.
- Preserve the private, self-hosted operating model.
