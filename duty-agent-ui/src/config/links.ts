/**
 * 对外链接的单一出处（「关于」页、隐私入口、商店列表共用）。
 *
 * 隐私政策已正式托管在 https://legal.helema.cn/ （免登录、HTTPS、中英双语），
 * 与仓库里的 PRIVACY.md 是同一份内容的发布版。**改政策时两边都要更新**：
 * 仓库改完要同步到托管页，否则商店审核读到的还是旧条款。
 *
 * Partner Center 的「隐私政策 URL」字段填的就是 PRIVACY_URL 这一个值。
 */
const REPO = 'https://github.com/helemazuba-boop/Duty-Agent';

/** 隐私政策（正式托管地址）。 */
export const PRIVACY_URL = 'https://legal.helema.cn/';

/** 使用说明 / 帮助。 */
export const SUPPORT_URL = `${REPO}#readme`;

/** 问题反馈入口（含 AI 输出不当的举报）。 */
export const FEEDBACK_URL = `${REPO}/issues`;

/** 私密反馈渠道：隐私问题与涉及真实学生数据的材料走邮件，不进公开 issue。 */
export const SUPPORT_EMAIL = 'helemazuba@gmail.com';
