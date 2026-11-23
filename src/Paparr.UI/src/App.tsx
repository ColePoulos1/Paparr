import { useCallback, useEffect, useState } from "react";
import { labels, request, statuses } from "./api";
import type { ImportJob, Page, Status, Summary } from "./api";
import ReviewPanel from "./components/ReviewPanel";
import LibraryHealth from "./components/LibraryHealth";
import WorkerHealth from "./components/WorkerHealth";

import { AppShell } from "@astryxdesign/core/AppShell";
import { Badge } from "@astryxdesign/core/Badge";
import { Banner } from "@astryxdesign/core/Banner";
import { Button } from "@astryxdesign/core/Button";
import { Card } from "@astryxdesign/core/Card";
import { EmptyState } from "@astryxdesign/core/EmptyState";
import { Heading } from "@astryxdesign/core/Heading";
import { Icon } from "@astryxdesign/core/Icon";
import { Item } from "@astryxdesign/core/Item";
import { Link } from "@astryxdesign/core/Link";
import { Pagination } from "@astryxdesign/core/Pagination";
import { SideNav, SideNavItem, SideNavSection } from "@astryxdesign/core/SideNav";
import { Text } from "@astryxdesign/core/Text";
import { TextInput } from "@astryxdesign/core/TextInput";
import { TopNav, TopNavHeading } from "@astryxdesign/core/TopNav";
import {
  Activity, ArchiveRestore, BookCheck, BookOpen, CircleAlert, Clock,
  FolderInput, Inbox, Library, ListChecks, LogOut, RefreshCw, ScanText, Search,
} from "lucide-react";
import "./workspace.css";

const statusIcons = {
  AwaitingApproval: ListChecks, Pending: Clock, Processing: ScanText, Publishing: FolderInput,
  Undoing: ArchiveRestore, Failed: CircleAlert, Completed: BookCheck,
} satisfies Record<Status, typeof Inbox>;

function ImportSearch({ initial, onSearch }: { initial: string; onSearch: (query: string) => void }) {
  const [query, setQuery] = useState(initial);
  return (
    <form className="import-search" role="search" onSubmit={(event) => {
      event.preventDefault();
      onSearch(query);
    }}>
      <TextInput label="Search imports" isLabelHidden startIcon={Search}
        value={query} onChange={setQuery} {...{ maxLength: 200 }} width="100%"
        placeholder="Search by title, author, or filename" />
      <Button type="submit" label="Search" variant="secondary" />
    </form>
  );
}

function locationView(): "imports" | "library" | "worker" {
  const view = new URLSearchParams(window.location.search).get("view");
  return view === "library" || view === "worker" ? view : "imports";
}

function locationFilter() {
  const params = new URLSearchParams(window.location.search);
  const value = params.get("status");
  return {
    status: statuses.includes(value as Status)
      ? (value as Status)
      : ("AwaitingApproval" as Status),
    search: params.get("q") ?? "",
    page: Math.min(
      100000,
      Math.max(1, Math.trunc(Number(params.get("page"))) || 1),
    ),
  };
}

interface AppProps {
  signOut?: () => Promise<void>;
}

export default function App({ signOut }: AppProps) {
  const [filter, setFilter] = useState(locationFilter);
  const [view, setView] = useState(locationView);
  const [result, setResult] = useState<Page>({
    items: [],
    total: 0,
    page: 1,
    pageSize: 25,
  });
  const [summary, setSummary] = useState<Summary | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<ImportJob | null>(null);
  const [detailError, setDetailError] = useState<{ id: number; message: string } | null>(null);
  const [draftJob, setDraftJob] = useState<ImportJob | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [signingOut, setSigningOut] = useState(false);
  const [undone, setUndone] = useState(false);
  const activeDraft = draftJob && (selectedId === null || draftJob.id === selectedId)
    ? draftJob
    : null;
  const selectedRow = result.items.find((job) => job.id === (selectedId ?? activeDraft?.id))
    ?? (activeDraft ? undefined : result.items[0]);
  const activeId = selectedRow?.id ?? activeDraft?.id;
  const selected = detail?.id === activeId ? detail : activeDraft;
  const reload = useCallback(() => setRefresh((n) => n + 1), []);

  useEffect(() => {
    const pop = () => {
      setDraftJob(null);
      setSelectedId(null);
      setFilter(locationFilter());
      setView(locationView());
    };
    window.addEventListener("popstate", pop);
    return () => window.removeEventListener("popstate", pop);
  }, []);

  useEffect(() => {
    const interval = window.setInterval(reload, 15000);
    return () => window.clearInterval(interval);
  }, [reload]);

  useEffect(() => {
    const controller = new AbortController();
    request<Summary>("/api/imports/summary", { signal: controller.signal })
      .then((counts) => { if (!controller.signal.aborted) setSummary(counts); })
      .catch(() => { if (!controller.signal.aborted) setSummary(null); });
    return () => controller.abort();
  }, [refresh, view]);

  useEffect(() => {
    if (view !== "imports") return;
    const controller = new AbortController();
    const params = new URLSearchParams({
      status: filter.status,
      search: filter.search,
      page: String(filter.page),
      pageSize: "25",
    });
    setLoading(true);
    request<Page>("/api/imports?" + params, { signal: controller.signal })
      .then((page) => {
        if (controller.signal.aborted) return;
        setResult(page);
        setError("");
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setError(reason.message);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [filter, refresh, view]);

  useEffect(() => {
    if (view !== "imports" || !selectedRow) return;
    const controller = new AbortController();
    const id = selectedRow.id;
    request<ImportJob>(`/api/imports/${id}`, { signal: controller.signal })
      .then((job) => {
        if (controller.signal.aborted) return;
        setDetail(job);
        setDetailError(null);
      })
      .catch((reason: Error) => {
        if (!controller.signal.aborted) setDetailError({ id, message: reason.message });
      });
    return () => controller.abort();
  }, [selectedRow, view]);

  function navigate(next: typeof filter) {
    setUndone(false);
    setView("imports");
    setDraftJob(null);
    setSelectedId(null);
    setFilter(next);
    const params = new URLSearchParams({ status: next.status });
    if (next.search) params.set("q", next.search);
    if (next.page > 1) params.set("page", String(next.page));
    window.history.pushState(null, "", "?" + params);
  }

  function changed(job: ImportJob) {
    if (job.status === "AwaitingApproval" && (selected?.status === "Completed" || selected?.status === "Undoing"))
      setUndone(true);
    setDetail((previous) => previous?.id === job.id ? job : previous);
    setDraftJob((previous) => (previous?.id === job.id ? null : previous));
    const book = job.acceptedBook ?? job.candidates[0];
    setResult((previous) => ({
      ...previous,
      items: previous.items.map((item) => item.id === job.id
        ? { ...item, status: job.status, title: book?.title ?? null, author: book?.author ?? null }
        : item),
    }));
    reload();
  }

  function openView(next: "library" | "worker") {
    setDraftJob(null);
    setSelectedId(null);
    setView(next);
    window.history.pushState(null, "", "?view=" + next);
  }

  const empty = result.items.length === 0 && !selected;
  return (
    <AppShell variant="wash" height="auto" contentPadding={0}
      topNav={
        <TopNav label="Paparr"
          heading={<TopNavHeading heading="Paparr" headingHref="/"
            logo={<img className="paparr-mark" src="/paparr.svg" alt="" />} />}
          endContent={signOut && <Button label={signingOut ? "Signing out…" : "Sign out"}
            icon={<Icon icon={LogOut} />} variant="ghost" isDisabled={signingOut}
            onClick={async () => {
              setSigningOut(true);
              try { await signOut(); }
              finally { setSigningOut(false); }
            }} />}
        />
      }
      sideNav={
        <SideNav aria-label="Import status">
          <SideNavSection title="Imports">
            {statuses.map((status) => <SideNavItem key={status}
              label={labels[status]} icon={statusIcons[status]}
              isSelected={view === "imports" && filter.status === status}
              endContent={<span className="queue-count">{summary?.[status] ?? "—"}</span>}
              onClick={() => navigate({ ...filter, status, page: 1 })} />)}
          </SideNavSection>
          <SideNavSection title="Maintenance">
            <SideNavItem label="Library health" icon={Library} isSelected={view === "library"}
              onClick={() => openView("library")} />
            <SideNavItem label="Worker status" icon={Activity} isSelected={view === "worker"}
              onClick={() => openView("worker")} />
          </SideNavSection>
        </SideNav>
      }
    >
      <div className="workspace-content">
        {view === "library" ? <LibraryHealth /> : view === "worker" ? <WorkerHealth /> : (
          <>
            <header className="queue-header">
              <Heading level={1}>{labels[filter.status]}</Heading>
              <Button label={loading ? "Refreshing…" : "Refresh"} variant="secondary"
                icon={<Icon icon={RefreshCw} />} onClick={reload} isDisabled={loading} />
            </header>

            <dl className="import-overview" aria-label="Import overview">
              <div><dt>Ready for review</dt><dd>{summary?.AwaitingApproval ?? "—"}</dd></div>
              <div><dt>In progress</dt><dd>{summary
                ? summary.Pending + summary.Processing + summary.Publishing + (summary.Undoing ?? 0)
                : "—"}</dd></div>
              <div><dt>Imported</dt><dd>{summary?.Completed ?? "—"}</dd></div>
            </dl>

            <ImportSearch key={filter.search} initial={filter.search}
              onSearch={(search) => navigate({ ...filter, search, page: 1 })} />
            {error && <Banner status="error" title={error} role="alert"
              endContent={<Button size="sm" label="Try again" variant="secondary" onClick={reload} />} />}
            {undone && <Banner status="success" role="status"
              title="The import returned to review. Your original file is retained."
              endContent={<Button size="sm" label="Review again" variant="secondary"
                onClick={() => navigate({ status: "AwaitingApproval", search: "", page: 1 })} />} />}
            {empty ? !error && (
              <Card padding={4}>
                {loading ? <Text as="p" role="status">Loading your imports…</Text> : (
                  <EmptyState headingLevel={2} icon={<Icon icon={filter.search ? Search : Inbox} size="lg" />}
                    title={filter.search ? "No matching books"
                      : filter.status === "AwaitingApproval" ? "Your review desk is clear" : "Nothing here yet"}
                    description={filter.search ? "Try another title, author, or filename."
                      : filter.status === "AwaitingApproval" || filter.status === "Pending"
                        ? "Add EPUB or PDF files to your watch folder to start an import."
                        : "Books will appear here when an import reaches this stage."}
                    actions={filter.search && <Button label="Clear search" variant="secondary"
                      onClick={() => navigate({ ...filter, search: "", page: 1 })} />}
                  />
                )}
              </Card>
            ) : (
              <div className="intake-panels">
                <section className="queue-list" aria-labelledby="queue-list-heading">
                  <div className="queue-list-header">
                    <Heading level={2} id="queue-list-heading">Books</Heading>
                    <Badge label={result.total} />
                    {result.items.length > 1 && <Link className="queue-detail-link" href="#selected-import">
                      View details
                    </Link>}
                  </div>
                  <div className="queue-rows">
                  {result.items.map((job) => (
                    <Item key={job.id} density="spacious"
                      label={job.title ?? job.fileName}
                      description={<>
                        <span className="queue-author">{job.author ?? "Metadata needed"}</span>
                        {job.title && <span className="queue-filename">{job.fileName}</span>}
                      </>}
                      startContent={<span className="queue-format" aria-hidden="true">
                        <Icon icon={BookOpen} size="md" />
                        {(job.fileName.split(".").pop() ?? "").toUpperCase()}
                      </span>}
                      isSelected={activeId === job.id}
                      onClick={() => {
                        if (activeId !== job.id) setDraftJob(null);
                        setSelectedId(job.id);
                      }}
                    />
                  ))}
                  {result.items.length === 0 && <Text as="p" type="supporting">No books in this queue.</Text>}
                  </div>
                </section>
                <div className="intake-detail" id="selected-import" tabIndex={-1}>
                  {selectedRow && detailError && detailError.id === activeId && (
                    <Banner status="error" role="alert" title={detailError.message}
                      endContent={<Button label="Try again" size="sm" variant="secondary" onClick={reload} />} />
                  )}
                  {selectedRow && !selected && detailError?.id !== activeId && (
                    <Card><Text as="p" role="status">Loading import details…</Text></Card>
                  )}
                  {selected && <ReviewPanel key={selected.id} job={selected} onChange={changed}
                    onDraftChange={(job, dirty) =>
                      setDraftJob((previous) => dirty ? job : previous?.id === job.id ? null : previous)}
                    reviewUnavailable={!selectedRow} />}
                </div>
              </div>
            )}
            {(result.total > 25 || filter.page > 1) && (
              <Pagination label="Import pages" variant="pages" page={filter.page} pageSize={25}
                totalPages={Math.max(filter.page, Math.ceil(result.total / 25))}
                isDisabled={loading} onChange={(page) => navigate({ ...filter, page })} />
            )}
          </>
        )}
      </div>
    </AppShell>
  );
}
