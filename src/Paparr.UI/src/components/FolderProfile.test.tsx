import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import FolderProfile from "./FolderProfile";

it("shows the captured routing and filename rule beside review metadata", () => {
  render(
    <FolderProfile
      profile={{
        name: "Research & papers",
        sourceFolder: "research/papers",
        collection: "Research",
        filenameOrder: "AuthorTitle",
      }}
    />,
  );
  expect(screen.getByRole("region", { name: "Folder profile" })).toBeVisible();
  expect(screen.getByText("research/papers")).toBeVisible();
  expect(screen.getByText("Research")).toBeVisible();
  expect(screen.getByText(/Author — Title/)).toHaveTextContent(
    "Embedded metadata is preferred",
  );
  expect(screen.getByText(/keeps the profile selected/)).toBeVisible();
});

it("supports a filename-only profile using the default library layout", () => {
  render(
    <FolderProfile
      profile={{
        name: "Downloads",
        sourceFolder: "downloads",
        collection: null,
        filenameOrder: "TitleAuthor",
      }}
    />,
  );
  expect(screen.getByText("Default library layout")).toBeVisible();
  expect(screen.getByText(/Title — Author/)).toBeVisible();
});

it("keeps default intake uncluttered", () => {
  render(
    <FolderProfile
      profile={{
        name: null,
        sourceFolder: null,
        collection: null,
        filenameOrder: "TitleAuthor",
      }}
    />,
  );
  expect(screen.queryByRole("region")).not.toBeInTheDocument();
});
