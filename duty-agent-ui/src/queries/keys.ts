export const snapshotKey = ['snapshot'] as const;
export const rosterKey = ['roster'] as const;
export const readinessKey = (probeModel = false) => ['readiness', probeModel] as const;
export const bridgeStatusKey = ['bridge-status'] as const;
export const configKey = ['config'] as const;
export const notificationSettingsKey = ['notification-settings'] as const;
