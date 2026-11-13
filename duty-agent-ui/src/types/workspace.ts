export interface WorkspaceState {
  schedule_pool: ScheduleEntry[];
  [key: string]: any;
}

export interface ScheduleEntry {
  date: string;
  day?: string;
  note?: string;
  area_assignments: Record<string, string[]>;
}

export interface RosterPerson {
  name: string;
  active: boolean;
  [key: string]: any;
}

export interface Workspace {
  roster: RosterPerson[];
  state: WorkspaceState;
  /** 旧版后端字段，仅为兼容保留；前端不再据此推算"今天"。 */
  component_refresh_time?: string;
}
