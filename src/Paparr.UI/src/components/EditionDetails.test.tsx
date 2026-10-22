import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import EditionDetails from "./EditionDetails";

it("shows absent and ambiguous ISBNs without inventing an identifier", () => {
  const { rerender } = render(<EditionDetails edition={null} />);
  expect(screen.getByText("No edition details supplied.")).toBeVisible();
  rerender(<EditionDetails edition={{
    isbn13: null, isbnStatus: "Ambiguous", publisher: null,
    publicationYear: 2020, language: "fr",
  }} />);
  expect(screen.getByText("Multiple ISBNs reported")).toBeVisible();
  expect(screen.getByText("2020")).toBeVisible();
  expect(screen.getByText("fr")).toBeVisible();
  expect(screen.getByText("Not supplied")).toBeVisible();
});

it("renders untrusted publisher text as text", () => {
  render(<EditionDetails edition={{
    isbn13: "9780306406157", isbnStatus: "Valid", publisher: "<script>publisher</script>",
    publicationYear: null, language: null,
  }} />);
  expect(screen.getByText("9780306406157")).toBeVisible();
  expect(screen.getByText("<script>publisher</script>")).toBeVisible();
  expect(document.querySelector("script")).toBeNull();
});
