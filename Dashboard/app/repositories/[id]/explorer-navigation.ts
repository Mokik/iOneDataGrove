export type EntityFocus = { type: string; id: number; label: string };

export function linksHref(entity: EntityFocus) {
  return `#${new URLSearchParams({ tab: "links", focusType: entity.type, focusId: String(entity.id), label: entity.label })}`;
}

export function sourceHref(path: string, line?: number | null) {
  const parameters = new URLSearchParams({ tab: "source", path });
  if (line) parameters.set("line", String(line));
  return `#${parameters}`;
}

export function fileLinksHref(path: string) {
  return `#${new URLSearchParams({ tab: "links", path })}`;
}

export function subscribeNavigation(callback: () => void) {
  window.addEventListener("hashchange", callback);
  return () => window.removeEventListener("hashchange", callback);
}

export const readNavigation = () => window.location.hash.slice(1);
export const emptyNavigation = () => "";
