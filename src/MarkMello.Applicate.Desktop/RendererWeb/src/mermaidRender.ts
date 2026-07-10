export type MermaidApiLike = {
  render: (id: string, source: string) => Promise<{ svg: string }>;
};

export type MermaidRenderOptions = {
  manageVirtualizedProxyLifecycle?: boolean;
};

const MERMAID_PROXY_LIFECYCLE_OWNER = Symbol("mm-mermaid-proxy-lifecycle-owner");

type MermaidProxyLifecycleClaim = {
  proxy: HTMLElement | null;
  source: HTMLElement;
  state: "pending" | "ready" | "superseded";
};

type LifecycleOwnedElement = HTMLElement & {
  [MERMAID_PROXY_LIFECYCLE_OWNER]?: MermaidProxyLifecycleClaim;
};

export function isMermaidNodeNearViewport(
  node: HTMLElement,
  viewportHeight: number,
  marginPx: number
): boolean {
  const rect = node.getBoundingClientRect();
  return rect.bottom >= -marginPx && rect.top <= viewportHeight + marginPx;
}

export async function renderMermaidNode(
  node: HTMLElement,
  generation: number,
  getCurrentGeneration: () => number,
  mermaid: MermaidApiLike,
  perDiagramTimeoutMs: number,
  options?: MermaidRenderOptions
): Promise<void> {
  const codeEl = node.querySelector<HTMLElement>("code[data-mm-mermaid]");
  if (!codeEl) return;
  const source = codeEl.textContent ?? "";
  const lifecycleClaim = options?.manageVirtualizedProxyLifecycle === true
    ? claimProxyLifecycle(node)
    : null;

  let timeoutHandle: ReturnType<typeof setTimeout> | undefined;
  try {
    const id = `mm-mermaid-${generation}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const timeoutPromise = new Promise<never>((_, reject) => {
      timeoutHandle = setTimeout(() => reject(new Error("mermaid render timeout")), perDiagramTimeoutMs);
    });
    const { svg } = await Promise.race([mermaid.render(id, source), timeoutPromise]);

    if (
      getCurrentGeneration() !== generation
      || (lifecycleClaim !== null && (!node.isConnected || !ownsLifecycleClaim(node, lifecycleClaim)))
    ) {
      if (lifecycleClaim !== null) {
        resetOwnedProxyLifecycle(node, lifecycleClaim);
      }
      return;
    }

    let svgHost = node.nextElementSibling as HTMLElement | null;
    if (!svgHost || !svgHost.classList.contains("mm-mermaid-svg")) {
      svgHost = node.ownerDocument.createElement("div");
      svgHost.className = "mm-mermaid-svg";
      node.after(svgHost);
    }
    if (lifecycleClaim !== null) {
      lifecycleClaim.proxy = svgHost;
      (svgHost as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] = lifecycleClaim;
      removeExtraAdjacentProxies(svgHost);
    }
    svgHost.innerHTML = svg;
    node.classList.add("is-rendered");
    if (lifecycleClaim !== null) {
      lifecycleClaim.state = "ready";
    }
  } catch {
    if (lifecycleClaim !== null) {
      resetOwnedProxyLifecycle(node, lifecycleClaim);
      return;
    }
    if (getCurrentGeneration() !== generation) return;
    node.classList.remove("is-rendered");
    const sibling = node.nextElementSibling;
    if (sibling?.classList.contains("mm-mermaid-svg")) sibling.remove();
  } finally {
    if (timeoutHandle !== undefined) clearTimeout(timeoutHandle);
  }
}

export function readReadyMermaidProxy(source: HTMLElement): HTMLElement | null {
  if (!source.matches("pre.mm-mermaid.is-rendered") || !source.isConnected) {
    return null;
  }

  const claim = (source as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER];
  const proxy = source.nextElementSibling;
  if (
    claim === undefined
    || claim.state !== "ready"
    || claim.source !== source
    || !(proxy instanceof HTMLElement)
    || claim.proxy !== proxy
    || (proxy as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] !== claim
    || !proxy.isConnected
    || proxy.parentElement !== source.parentElement
    || !proxy.classList.contains("mm-mermaid-svg")
    || proxy.hasAttribute("data-mm-block-index")
    || proxy.nextElementSibling?.classList.contains("mm-mermaid-svg")
  ) {
    return null;
  }

  const sourceHeight = source.offsetHeight;
  const proxyHeight = proxy.offsetHeight;
  const sourceStyle = readComputedStyle(source);
  const proxyStyle = readComputedStyle(proxy);
  const sourceIsHiddenOrZeroBox = Number.isFinite(sourceHeight)
    && (sourceStyle?.display === "none" || sourceHeight <= 0);
  const proxyIsVisible = proxyStyle?.display !== "none"
    && proxyStyle?.visibility !== "hidden"
    && proxyStyle?.visibility !== "collapse";
  if (
    !sourceIsHiddenOrZeroBox
    || !Number.isFinite(proxyHeight)
    || proxyHeight <= 0
    || !proxyIsVisible
  ) {
    return null;
  }

  return proxy;
}

export function reclaimClonedMermaidProxyLifecycles(root: ParentNode): void {
  const sources = root.querySelectorAll<HTMLElement>("pre.mm-mermaid.is-rendered");
  for (const source of sources) {
    if (readReadyMermaidProxy(source) !== null) {
      continue;
    }

    const proxy = source.nextElementSibling;
    const hasValidAdjacency = proxy instanceof HTMLElement
      && proxy.parentElement === source.parentElement
      && proxy.classList.contains("mm-mermaid-svg")
      && !proxy.hasAttribute("data-mm-block-index")
      && !proxy.nextElementSibling?.classList.contains("mm-mermaid-svg");
    if (hasValidAdjacency) {
      const claim: MermaidProxyLifecycleClaim = {
        proxy,
        source,
        state: "ready",
      };
      (source as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
      (proxy as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
      continue;
    }

    source.classList.remove("is-rendered");
    let sibling = source.nextElementSibling;
    while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
      const nextSibling = sibling.nextElementSibling;
      sibling.remove();
      sibling = nextSibling;
    }
  }
}

function claimProxyLifecycle(node: HTMLElement): MermaidProxyLifecycleClaim {
  const previousClaim = (node as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER];
  if (previousClaim !== undefined) {
    previousClaim.state = "superseded";
  }

  const claim: MermaidProxyLifecycleClaim = {
    proxy: null,
    source: node,
    state: "pending",
  };
  (node as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
  let sibling = node.nextElementSibling;
  while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
    (sibling as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] = claim;
    sibling = sibling.nextElementSibling;
  }
  return claim;
}

function ownsLifecycleClaim(node: HTMLElement, claim: MermaidProxyLifecycleClaim): boolean {
  return claim.state === "pending"
    && claim.source === node
    && (node as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] === claim;
}

function resetOwnedProxyLifecycle(node: HTMLElement, claim: MermaidProxyLifecycleClaim): void {
  const ownedNode = node as LifecycleOwnedElement;
  if (ownedNode[MERMAID_PROXY_LIFECYCLE_OWNER] !== claim) {
    return;
  }

  claim.state = "superseded";
  delete ownedNode[MERMAID_PROXY_LIFECYCLE_OWNER];
  node.classList.remove("is-rendered");
  let sibling = node.nextElementSibling;
  while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
    const nextSibling = sibling.nextElementSibling;
    if ((sibling as LifecycleOwnedElement)[MERMAID_PROXY_LIFECYCLE_OWNER] === claim) {
      sibling.remove();
    }
    sibling = nextSibling;
  }
}

function readComputedStyle(element: HTMLElement): CSSStyleDeclaration | null {
  return element.ownerDocument.defaultView?.getComputedStyle(element) ?? null;
}

function removeExtraAdjacentProxies(proxy: HTMLElement): void {
  let sibling = proxy.nextElementSibling;
  while (sibling instanceof HTMLElement && sibling.classList.contains("mm-mermaid-svg")) {
    const nextSibling = sibling.nextElementSibling;
    sibling.remove();
    sibling = nextSibling;
  }
}
