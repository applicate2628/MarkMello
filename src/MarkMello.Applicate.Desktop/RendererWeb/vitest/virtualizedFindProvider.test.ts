import { describe, expect, it } from "vitest";
import type { DocumentWindowModel } from "../src/documentWindow";
import type { FindProviderStatus } from "../src/findBar";
import {
  createVirtualizedFindProvider,
  type FindMatchDescriptor,
  type FindQueryMessage,
  type FindResultsMessage,
  type VirtualizedFindContext,
} from "../src/virtualizedFindProvider";

function descriptorForBlock(blockIndex: number, ordinal: number): FindMatchDescriptor {
  return {
    blockIndex,
    blockLocalOffset: 0,
    length: "needle".length,
    matchId: `block-${blockIndex}-match-${ordinal}`,
    normalizedText: "needle",
    ordinal,
  };
}

function makeModel(): DocumentWindowModel {
  return {
    getEntryByBlockIndex: blockIndex => ({ blockIndex, sectionIndex: blockIndex }),
    getEntryContainingBlockIndex: blockIndex => ({ blockIndex, sectionIndex: blockIndex }),
    sectionEffectiveHeight: () => 80,
    sectionTop: sectionIndex => sectionIndex * 100,
  } as unknown as DocumentWindowModel;
}

describe("virtualized find provider", () => {
  it("tags flag-on queries with the rendered DOM text domain", () => {
    const hostMessages: FindQueryMessage[] = [];
    const provider = createVirtualizedFindProvider({
      postHostMessage: message => { hostMessages.push(message); },
      readContext: () => ({
        beginNavigationOperation: () => null,
        completeNavigationOperation: () => { },
        controller: null,
        main: null,
        model: makeModel(),
        ownerWindow: window,
        renderId: 5,
        root: document.documentElement,
        virtualizationEnabled: true,
      }),
    });

    provider.search("needle");

    expect(hostMessages).toEqual([
      expect.objectContaining({
        query: "needle",
        renderId: 5,
        textDomain: "rendered-dom-v1",
        type: "find-query",
      }),
    ]);
  });

  it.each(["pending", "unavailable"] as const)(
    "keeps rendered-domain %s results empty and inert",
    status => {
      const hostMessages: FindQueryMessage[] = [];
      const statuses: FindProviderStatus[] = [];
      const provider = createVirtualizedFindProvider({
        postHostMessage: message => { hostMessages.push(message); },
        readContext: () => ({
          beginNavigationOperation: () => null,
          completeNavigationOperation: () => { },
          controller: null,
          main: null,
          model: makeModel(),
          ownerWindow: window,
          renderId: 5,
          root: document.documentElement,
          virtualizationEnabled: true,
        }),
      });
      provider.setView({ updateStatus: value => { statuses.push(value); } });
      provider.search("needle");

      provider.handleFindResults({
        matches: [descriptorForBlock(1, 1)],
        query: "needle",
        renderId: 5,
        requestId: hostMessages.at(-1)!.requestId,
        status,
        textDomain: "rendered-dom-v1",
        totalCount: 1,
        type: "find-results",
      } as FindResultsMessage);

      expect(statuses.at(-1)).toMatchObject({ currentIndex: -1, totalCount: 0 });
    }
  );

  it("drops a rendered-domain descriptor whose offset misses without normalized-text repair", () => {
    document.body.innerHTML = '<main class="mm-document"><p data-mm-block-index="1">needle</p></main>';
    const hostMessages: FindQueryMessage[] = [];
    const statuses: FindProviderStatus[] = [];
    const provider = createVirtualizedFindProvider({
      postHostMessage: message => { hostMessages.push(message); },
      readContext: () => ({
        beginNavigationOperation: () => null,
        completeNavigationOperation: () => { },
        controller: null,
        main: document.querySelector("main"),
        model: makeModel(),
        ownerWindow: window,
        renderId: 5,
        root: document.documentElement,
        virtualizationEnabled: true,
      }),
    });
    provider.setView({ updateStatus: value => { statuses.push(value); } });
    provider.search("needle");

    provider.handleFindResults({
      matches: [{ ...descriptorForBlock(1, 1), blockLocalOffset: 99 }],
      query: "needle",
      renderId: 5,
      requestId: hostMessages.at(-1)!.requestId,
      status: "ready",
      textDomain: "rendered-dom-v1",
      totalCount: 1,
      type: "find-results",
    } as FindResultsMessage);

    expect(statuses.at(-1)).toMatchObject({ currentIndex: -1, totalCount: 1 });
  });

  it("starts host result navigation at the global first match independent of reading position", () => {
    const hostMessages: FindQueryMessage[] = [];
    const statuses: FindProviderStatus[] = [];
    const root = {
      clientHeight: 200,
      scrollHeight: 10_000,
      scrollTop: 5_000,
    } as Element & { clientHeight: number; scrollHeight: number; scrollTop: number };
    const context: VirtualizedFindContext = {
      beginNavigationOperation: () => null,
      completeNavigationOperation: () => { },
      controller: null,
      main: null,
      model: makeModel(),
      ownerWindow: window,
      renderId: 5,
      root,
      virtualizationEnabled: false,
    };
    const provider = createVirtualizedFindProvider({
      postHostMessage: message => { hostMessages.push(message); },
      readContext: () => context,
    });

    provider.setView({ updateStatus: status => { statuses.push(status); } });
    provider.search("needle");
    provider.handleFindResults({
      matches: [descriptorForBlock(10, 1), descriptorForBlock(90, 2)],
      query: "needle",
      renderId: 5,
      requestId: hostMessages.at(-1)!.requestId,
      status: "ready",
      textDomain: "rendered-dom-v1",
      totalCount: 2,
      type: "find-results",
    });

    expect(statuses.at(-1)).toMatchObject({
      currentIndex: 0,
      query: "needle",
      totalCount: 2,
    });
  });
});
