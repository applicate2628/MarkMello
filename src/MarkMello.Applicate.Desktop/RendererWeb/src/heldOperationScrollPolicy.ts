export type HeldOperationIdentity = Readonly<{
  documentEpoch: number;
  operationEpoch: number;
}>;

export type ActiveHeldOperationMode = "gesture" | "navigation" | "restore";

export type ActiveHeldOperationState = Readonly<{
  mode: ActiveHeldOperationMode;
}>;

export type ActiveHeldOperationRegistration<TState extends ActiveHeldOperationState> =
  HeldOperationIdentity & TState;

export type HeldOperationTargetResolution<TState extends ActiveHeldOperationState> =
  | Readonly<{
      kind: "active";
      registration: ActiveHeldOperationRegistration<TState>;
    }>
  | Readonly<{ kind: "generic" }>;

export type HeldOperationScrollPolicy<TState extends ActiveHeldOperationState> = Readonly<{
  clear: (identity: HeldOperationIdentity) => boolean;
  read: (identity: HeldOperationIdentity) => ActiveHeldOperationRegistration<TState> | null;
  readActive: () => ActiveHeldOperationRegistration<TState> | null;
  register: (
    identity: HeldOperationIdentity,
    state: TState
  ) => ActiveHeldOperationRegistration<TState>;
  resolve: (identity: HeldOperationIdentity) => HeldOperationTargetResolution<TState>;
  update: (identity: HeldOperationIdentity, state: TState) => boolean;
}>;

export function createHeldOperationScrollPolicy<
  TState extends ActiveHeldOperationState
>(): HeldOperationScrollPolicy<TState> {
  let active: ActiveHeldOperationRegistration<TState> | null = null;

  const matches = (identity: HeldOperationIdentity): boolean =>
    active !== null
    && active.documentEpoch === identity.documentEpoch
    && active.operationEpoch === identity.operationEpoch;

  return {
    clear: identity => {
      if (!matches(identity)) {
        return false;
      }
      active = null;
      return true;
    },
    read: identity => matches(identity) ? active : null,
    readActive: () => active,
    register: (identity, state) => {
      active = Object.freeze({
        documentEpoch: identity.documentEpoch,
        operationEpoch: identity.operationEpoch,
        ...state,
      }) as ActiveHeldOperationRegistration<TState>;
      return active;
    },
    resolve: identity => {
      if (matches(identity) && active !== null) {
        return { kind: "active", registration: active };
      }
      return { kind: "generic" };
    },
    update: (identity, state) => {
      if (!matches(identity) || active === null) {
        return false;
      }
      active = Object.freeze({
        documentEpoch: active.documentEpoch,
        operationEpoch: active.operationEpoch,
        ...state,
      }) as ActiveHeldOperationRegistration<TState>;
      return true;
    },
  };
}
