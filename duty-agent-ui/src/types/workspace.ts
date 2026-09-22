export interface WorkspaceState {
  schedule_pool: ScheduleEntry[];
  [key: string]: any;
}

export interface ScheduleEntry {
  date: string;
  day?: number;
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
  /** component_refresh_time (HH:MM)，来自 host-config.json。
   *  用于前端本地计算"当前效值日"（过了此时间即推进到下一天）。 */
  component_refresh_time?: string;
}
