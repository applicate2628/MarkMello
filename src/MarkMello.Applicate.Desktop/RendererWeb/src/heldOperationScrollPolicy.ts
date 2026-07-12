export type HeldOperationIdentity = Readonly<{
  documentEpoch: number;
  operationEpoch: number;
}>;

export type ActiveHeldOperationMode = "gesture" | "navigation" | "restore";

export type ActiveHeldOperationRegistration<TTarget> = HeldOperationIdentity & Readonly<{
  mode: ActiveHeldOperationMode;
  target: TTarget;
}>;

export type HeldOperationTargetResolution<TTarget> =
  | Readonly<{
      kind: "active";
      registration: ActiveHeldOperationRegistration<TTarget>;
    }>
  | Readonly<{
      kind: "retained-navigation";
      target: TTarget;
    }>
  | Readonly<{ kind: "generic" }>;

export type HeldOperationScrollPolicy<TTarget> = Readonly<{
  clear: (identity: HeldOperationIdentity) => boolean;
  read: (identity: HeldOperationIdentity) => ActiveHeldOperationRegistration<TTarget> | null;
  readActive: () => ActiveHeldOperationRegistration<TTarget> | null;
  register: (
    identity: HeldOperationIdentity,
    mode: ActiveHeldOperationMode,
    target: TTarget
  ) => ActiveHeldOperationRegistration<TTarget>;
  resolve: (
    identity: HeldOperationIdentity,
    retainedNavigationTarget: TTarget | null
  ) => HeldOperationTargetResolution<TTarget>;
  update: (identity: HeldOperationIdentity, target: TTarget) => boolean;
}>;

export function createHeldOperationScrollPolicy<TTarget>(): HeldOperationScrollPolicy<TTarget> {
  let active: ActiveHeldOperationRegistration<TTarget> | null = null;

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
    register: (identity, mode, target) => {
      active = Object.freeze({
        documentEpoch: identity.documentEpoch,
        mode,
        operationEpoch: identity.operationEpoch,
        target,
      });
      return active;
    },
    resolve: (identity, retainedNavigationTarget) => {
      if (matches(identity) && active !== null) {
        return { kind: "active", registration: active };
      }
      return retainedNavigationTarget === null
        ? { kind: "generic" }
        : { kind: "retained-navigation", target: retainedNavigationTarget };
    },
    update: (identity, target) => {
      if (!matches(identity) || active === null) {
        return false;
      }
      active = Object.freeze({ ...active, target });
      return true;
    },
  };
}
