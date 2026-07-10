export type H3DiagnosticMonitor = {
  dispose: () => void;
  registerCausalEpoch: (geometryEpoch: number) => void;
  sample: (height: number, geometryEpoch: number) => void;
};

export function createH3DiagnosticMonitor(initialHeight: number, tolerancePx = 1): H3DiagnosticMonitor {
  const causalGeometryEpochs = new Set<number>();
  let disposed = false;
  let previousHeight = initialHeight;

  return {
    dispose: () => {
      disposed = true;
      causalGeometryEpochs.clear();
    },
    registerCausalEpoch: geometryEpoch => {
      if (!disposed) causalGeometryEpochs.add(geometryEpoch);
    },
    sample: (height, geometryEpoch) => {
      if (disposed) return;
      const moved = Math.abs(height - previousHeight) > tolerancePx;
      previousHeight = height;
      if (moved && !causalGeometryEpochs.delete(geometryEpoch)) {
        throw new Error(`unregistered late geometry mover at epoch ${geometryEpoch}`);
      }
    },
  };
}
