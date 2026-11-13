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
  /** 值日预览规则：到达该边界后展示次日值日。只影响展示，不改"今天"。 */
  component_refresh_time?: string;
  duty_preview_enabled?: boolean;
  /** 后端诊断字段；客户端必须从本地时钟现算，不得拿它渲染（缓存会过期）。 */
  display_duty_date?: string;
  display_is_preview?: boolean;
}
