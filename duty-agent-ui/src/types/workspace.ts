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
}
