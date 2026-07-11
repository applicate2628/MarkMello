import { describe, expect, it } from "vitest";
import type { DocumentWindowModel } from "../src/documentWindow";
import type { FindProviderStatus } from "../src/findBar";
import {
  createVirtualizedFindProvider,
  type FindMatchDescriptor,
  type FindQueryMessage,
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
